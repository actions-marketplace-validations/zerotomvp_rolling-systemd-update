using ICSharpCode.SharpZipLib.GZip;
using ICSharpCode.SharpZipLib.Tar;
using Renci.SshNet;
using Renci.SshNet.Common;

namespace ZeroToMvp.Github.Actions.RollingSystemdUpdate;

class Updater : IDisposable
{
    private readonly SshClient ssh;
    private readonly SftpClient sftp;

    public Updater(UpdaterArguments args)
    {
        Args = args;

        var connectionInfo = new ConnectionInfo(args.Host, args.Port, args.Username, args.Auth);

        ssh = new SshClient(connectionInfo);
        sftp = new SftpClient(connectionInfo);

        if (Args.ExpectedFingerprint != null)
        {
            ssh.HostKeyReceived += OnHostKeyReceived!;
            sftp.HostKeyReceived += OnHostKeyReceived!;
        }
    }

    public UpdaterArguments Args { get; set; }

    public void Execute()
    {
        ssh.Connect();

        // get service definition first, it could fail

        var serviceDef = GetServiceDefinition();

        string lastPath = $"{serviceDef.WorkingDirectory}.last";

        // copy files

        sftp.Connect();

        string runNumber = Environment.GetEnvironmentVariable("GITHUB_RUN_NUMBER")!;
        string runAttempt = Environment.GetEnvironmentVariable("GITHUB_RUN_ATTEMPT")!;

        string tmp = $"/tmp/{Args.ServiceName}.{runNumber}.{runAttempt}";

        TransferFilesRecursively(tmp);

        // run update

        if (IsServiceRunning())
        {
            StopService();
        }

        // remove last, if exists

        if (DirectoryExists(lastPath))
        {
            ForceRemovePath(lastPath);
        }

        // move current to last, if exists

        if (DirectoryExists(serviceDef.WorkingDirectory))
        {
            MovePath(serviceDef.WorkingDirectory, lastPath);
        }

        // move temp to current

        MovePath(tmp, serviceDef.WorkingDirectory);

        // set permissions + ownership

        SetPermissions(serviceDef.WorkingDirectory, "774");
        RecursivelySetOwnership(serviceDef.WorkingDirectory, serviceDef.User);

        // start service

        StartService();

        // wait for it to come up

        var firstBinding = serviceDef.Bindings.First();

        for (int i = 0; i < 60; ++i)
        {
            if (CheckHttpStatus(firstBinding))
            {
                Console.WriteLine("Update completed.");
                return;
            }

            Thread.Sleep(1000);
        }

        throw new InvalidOperationException("Service didn't start after update");
    }

    public void Rollback()
    {
        var serviceDef = GetServiceDefinition();

        string lastPath = $"{serviceDef.WorkingDirectory}.last";

        if (Args.Debug)
        {
            Console.WriteLine($"WorkingDirectory={serviceDef.WorkingDirectory}");
            Console.WriteLine($"User={serviceDef.User}");
            Console.WriteLine($"Bindings={string.Join(",", serviceDef.Bindings)}");
        }

        CheckRequirements();

        // if there isn't a last directory, quit

        if (!DirectoryExists(lastPath))
        {
            Console.WriteLine("Rollback not possible because directory with previous installation does not exist, aborting...");
            return;
        }

        // stop service

        if (IsServiceRunning())
        {
            StopService();
        }

        // delete current

        ForceRemovePath(serviceDef.WorkingDirectory);

        // move last to current

        MovePath(lastPath, serviceDef.WorkingDirectory);

        if (Args.Debug)
        {
            RunAndLogCommand($"ls -l {serviceDef.WorkingDirectory}");
        }
        
        // start

        StartService();

        // wait for it to come up

        var firstBinding = serviceDef.Bindings.First();

        for (int i = 0; i < 60; ++i)
        {
            if (CheckHttpStatus(firstBinding))
            {
                Console.WriteLine("Rollback completed.");
                return;
            }

            Thread.Sleep(1000);
        }

        throw new InvalidOperationException("Service didn't start after rollback");
    }

    private void CheckRequirements()
    {
        if (ssh.RunCommand("which curl &> /dev/null").ExitStatus != 0)
        {
            throw new InvalidOperationException(
                "curl needs to be installed on the target host(s)");
        }
    }

    ServiceDefinition GetServiceDefinition()
    {
        var cmd = ssh.RunCommand($"cat /etc/systemd/system/{Args.ServiceName}.service");

        var kvs = Utils.ParseKeyValues(cmd.Result);

        try
        {
            return new()
            {
                WorkingDirectory = kvs["WorkingDirectory"],
                User = kvs["User"],
                Bindings = Utils.ParseBindings(kvs["Environment_ASPNETCORE_URLS"]).ToArray()
            };
        }
        catch (KeyNotFoundException)
        {
            throw new InvalidOperationException(
                $"The service definition must define the WorkingDirectory and the ASPNETCORE_URLS env variable; found: {cmd.Result}");
        }
    }

    private void ForceRemovePath(string path)
    {
        RunAndLogCommand($"rm -rf {path}");
    }

    private void MovePath(string src, string dest)
    {
        RunAndLogCommand($"mv {src} {dest}");
    }

    private void SetPermissions(string dest, string permissions)
    {
        RunAndLogCommand($"chmod {permissions} {dest}");
    }

    private void RecursivelySetOwnership(string dest, string owner)
    {
        RunAndLogCommand($"chown -R {owner}:{owner} {dest}");
    }

    private bool IsServiceRunning()
    {
        var cmd = RunAndLogCommand($"systemctl is-active {Args.ServiceName}");

        string status = cmd.Result.Trim();

        return status is not "inactive" and not "failed";
    }

    private bool DirectoryExists(string path)
    {
        var cmd = RunAndLogCommand($"test -d {path}");

        return cmd.ExitStatus == 0;
    }

    private void StopService()
    {
        RunAndLogCommand($"systemctl stop {Args.ServiceName}");
    }

    private void StartService()
    {
        RunAndLogCommand($"systemctl start {Args.ServiceName}");
    }

    private bool CheckHttpStatus(string binding, string path = "/api/health", int expectedStatus = 200)
    {
        var cmd = RunAndLogCommand($"curl --write-out \"%{{http_code}}\" --output /dev/null --silent {binding}{path}");

        return cmd.Result == expectedStatus.ToString();
    }

    private void TransferFilesRecursively(string dest)
    {
        string src = Args.SourceDirectory;
        var files = Utils.EnumerateFilesRecursively(src).ToArray();

        if(!files.Any())
        {
            throw new InvalidOperationException("No files found in source directory.");
        }

        Console.WriteLine($"Found {files.Length} files in {src}, creating tar archive...");

        string tgzLocal = $"{Path.GetFileName(dest)}.tgz";
        string tgzDest = $"{dest}.tgz";

        using (var outStream = File.Create(tgzLocal))
        using (var gzoStream = new GZipOutputStream(outStream))
        using (var tarArchive = TarArchive.CreateOutputTarArchive(gzoStream))
        {
            foreach (var file in files)
            {
                var tarEntry = TarEntry.CreateEntryFromFile(file.Src);

                tarEntry.Name = file.Path;

                tarArchive.WriteEntry(tarEntry, true);
            }
        } 

        Console.WriteLine($"Local archive is {new FileInfo(tgzLocal).Length:###,###} bytes.");
        
        UploadFile(tgzLocal, tgzDest);

        // this is required by tar

        RunAndLogCommand($"mkdir -p {dest}");

        var extract = RunAndLogCommand($"tar xf {tgzDest} -C {dest}");

        if (extract.ExitStatus != 0)
        {
            throw new InvalidOperationException("Unable to extract files.");
        }

        if (!Args.Debug)
        {
            ForceRemovePath(tgzDest);
        }
        else
        {
            RunAndLogCommand($"ls -l {tgzDest}");
            RunAndLogCommand($"ls -l {dest}");
        }
    }

    private void UploadFile(string src, string dest)
    {
        Console.WriteLine($"Transfering file {src} => {dest}");

        using var fileStream = File.OpenRead(src);

        sftp.UploadFile(fileStream, dest, progress =>
        {
            if (Args.Debug)
            {
                Console.WriteLine($"Transferred {progress:###,###} bytes.");
            }
        });

        if (Args.Debug)
        {
            RunAndLogCommand($"stat -c '%s' {dest}");
        }
    }
    
    private SshCommand RunAndLogCommand(string commandText)
    {
        var command = ssh.RunCommand(commandText);

        Console.WriteLine($"$ {commandText} (exit={command.ExitStatus})");

        if (!string.IsNullOrWhiteSpace(command.Result))
        {
            Console.WriteLine("\t{0}", command.Result);
        }

        if (!string.IsNullOrWhiteSpace(command.Error))
        {
            Console.WriteLine($"Failed; exit code = {command.ExitStatus}");
            Console.WriteLine(command.Error);
        }

        return command;
    }

    private void OnHostKeyReceived(object sender, HostKeyEventArgs e)
    {
        e.CanTrust = e.FingerPrint.SequenceEqual(Args.ExpectedFingerprint!);
    }

    public void Dispose()
    {
        try
        {
            ssh.Dispose();
        } catch { }

        try
        {
            sftp.Dispose();
        } catch { }
    }
}