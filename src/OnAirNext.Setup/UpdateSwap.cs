namespace OnAirNext.Installation;

// A reviewed image includes the unchanged encoder runtime as well as the application.
public sealed record UpdateImage(string BundleSha256,SetupFile[] RuntimeFiles);
public enum UpdateDiskState { Original, Prepared, Released, Applied }

// Only a protected, serialized installer calls this helper. It never opens engine data,
// stops a service, resolves a user-supplied backup path, deletes a tree or trusts a phase label.
public sealed class UpdateSwap
{
    public string InstallRoot { get; }
    public string CandidateRoot { get; }
    public string PreviousRoot { get; }
    private readonly UpdateImage _original,_replacement;
    public UpdateSwap(string trustedInstallRoot,string operationId,UpdateImage original,UpdateImage replacement)
    {
        if(!Guid.TryParseExact(operationId,"N",out _))throw new ArgumentException("Choose a saved update operation.");
        InstallRoot=SetupPaths.CanonicalRoot(trustedInstallRoot);
        CandidateRoot=SetupPaths.CanonicalRoot(InstallRoot+".candidate-"+operationId);
        PreviousRoot=SetupPaths.CanonicalRoot(InstallRoot+".previous-"+operationId);
        foreach(var path in new[]{CandidateRoot,PreviousRoot})
            if(!string.Equals(Path.GetDirectoryName(path),Path.GetDirectoryName(InstallRoot),StringComparison.OrdinalIgnoreCase))throw new IOException("Update folders must share the installation parent.");
        Validate(original);Validate(replacement);
        if(original.BundleSha256.Equals(replacement.BundleSha256,StringComparison.OrdinalIgnoreCase))throw new ArgumentException("The selected package is already installed.");
        // Application maintenance cannot substitute an encoder or its dependent DLLs.
        if(!SameRuntime(original.RuntimeFiles,replacement.RuntimeFiles))throw new ArgumentException("An application update must retain the reviewed encoder runtime.");
        _original=original with{RuntimeFiles=original.RuntimeFiles.ToArray()};_replacement=replacement with{RuntimeFiles=replacement.RuntimeFiles.ToArray()};
    }
    private static void Validate(UpdateImage image)
    {
        if(image is null||image.BundleSha256 is null||image.BundleSha256.Length!=64||!image.BundleSha256.All(Uri.IsHexDigit))throw new ArgumentException("The saved application identity is invalid.");
        SetupPaths.ValidateFiles(image.RuntimeFiles,false);
        foreach(var file in image.RuntimeFiles)SetupPaths.Resolve(Path.GetTempPath(),file.Path);
    }
    private static bool SameRuntime(SetupFile[] left,SetupFile[] right)
    {
        if(left.Length!=right.Length)return false;
        var sorted=right.ToDictionary(f=>f.Path,StringComparer.OrdinalIgnoreCase);
        return left.All(f=>sorted.TryGetValue(f.Path,out var item)&&f.Bytes==item.Bytes&&f.Sha256.Equals(item.Sha256,StringComparison.OrdinalIgnoreCase));
    }
    public static async Task<SetupBundle> VerifyImageAsync(string root,UpdateImage image,CancellationToken token=default)
    {
        Validate(image);root=SetupPaths.CanonicalRoot(root);
        var bundle=await SetupPaths.VerifyBundleAsync(root,image.BundleSha256,token);
        var expected=new HashSet<string>(bundle.Files.Select(f=>f.Path),StringComparer.OrdinalIgnoreCase){"bundle.json"};
        foreach(var file in image.RuntimeFiles)
        {
            var relative="tools/"+file.Path;if(!expected.Add(relative))throw new IOException("Image file identities overlap.");
            var full=SetupPaths.Resolve(root,relative);
            if(new FileInfo(full).Length!=file.Bytes||!string.Equals(await SetupPaths.HashAsync(full,token),file.Sha256,StringComparison.OrdinalIgnoreCase))throw new IOException("An installed runtime file differs from its reviewed identity.");
        }
        // Enumeration is bounded and rejects links before descending. Unknown files are
        // retained by refusing the operation rather than being silently discarded.
        var folders=new Stack<string>();folders.Push(root);var count=0;
        while(folders.TryPop(out var directory))
        {
            token.ThrowIfCancellationRequested();SetupPaths.RequireNoLinks(directory);
            foreach(var path in Directory.EnumerateFileSystemEntries(directory))
            {
                SetupPaths.RequireNoLinks(path);if(++count>4096)throw new IOException("The application image contains too many entries.");
                if(Directory.Exists(path)){folders.Push(path);continue;}
                var relative=Path.GetRelativePath(root,path).Replace(Path.DirectorySeparatorChar,'/');
                if(!expected.Remove(relative))throw new IOException("The application image contains an unreviewed file.");
            }
        }
        if(expected.Count!=0)throw new IOException("The reviewed application image is incomplete.");return bundle;
    }
    public async Task<UpdateDiskState> InspectAsync(CancellationToken token=default)
    {
        foreach(var path in new[]{InstallRoot,CandidateRoot,PreviousRoot}){SetupPaths.RequireNoLinks(path);if(File.Exists(path))throw new IOException("An update folder was replaced by a file.");}
        var root=Directory.Exists(InstallRoot);var candidate=Directory.Exists(CandidateRoot);var previous=Directory.Exists(PreviousRoot);
        if(root&&!previous)
        {
            await VerifyImageAsync(InstallRoot,_original,token);
            if(candidate)await VerifyImageAsync(CandidateRoot,_replacement,token);
            return candidate?UpdateDiskState.Prepared:UpdateDiskState.Original;
        }
        if(!root&&candidate&&previous)
        {
            await VerifyImageAsync(PreviousRoot,_original,token);await VerifyImageAsync(CandidateRoot,_replacement,token);return UpdateDiskState.Released;
        }
        if(root&&!candidate&&previous)
        {
            await VerifyImageAsync(InstallRoot,_replacement,token);await VerifyImageAsync(PreviousRoot,_original,token);return UpdateDiskState.Applied;
        }
        throw new IOException("Update folders do not match a recoverable reviewed state. Retain all folders and inspect the update receipt.");
    }
    // The caller durably records the requested direction BEFORE invoking either method,
    // and holds setup.lock plus a verified service-maintenance fence throughout.
    public async Task ApplyAsync(Func<Task> requireStopped,Func<string,Task> checkpoint,CancellationToken token=default)
    {
        var state=await InspectAsync(token);if(state==UpdateDiskState.Applied)return;
        if(state==UpdateDiskState.Original)throw new InvalidOperationException("Prepare and verify the replacement image first.");
        if(state==UpdateDiskState.Prepared)
        {
            await checkpoint("Releasing original application");await requireStopped();token.ThrowIfCancellationRequested();
            Directory.Move(InstallRoot,PreviousRoot);await checkpoint("Original application retained");
        }
        // Reinspect real bytes and folder ownership after an interrupted rename. A saved
        // phase alone can never authorize replacing an installation.
        if(await InspectAsync(token)!=UpdateDiskState.Released)throw new IOException("The update layout changed before activation.");
        await checkpoint("Activating reviewed application");await requireStopped();token.ThrowIfCancellationRequested();
        Directory.Move(CandidateRoot,InstallRoot);await checkpoint("Application activated");
        if(await InspectAsync(token)!=UpdateDiskState.Applied)throw new IOException("The activated application could not be verified.");
    }
    public async Task RollbackAsync(Func<Task> requireStopped,Func<string,Task> checkpoint,CancellationToken token=default)
    {
        var state=await InspectAsync(token);if(state is UpdateDiskState.Original or UpdateDiskState.Prepared)return;
        if(state==UpdateDiskState.Applied)
        {
            await checkpoint("Retaining replacement application");await requireStopped();token.ThrowIfCancellationRequested();
            Directory.Move(InstallRoot,CandidateRoot);await checkpoint("Replacement application retained");
        }
        if(await InspectAsync(token)!=UpdateDiskState.Released)throw new IOException("The update layout changed before rollback.");
        await checkpoint("Restoring original application");await requireStopped();token.ThrowIfCancellationRequested();
        Directory.Move(PreviousRoot,InstallRoot);await checkpoint("Original application restored");
        if(await InspectAsync(token)!=UpdateDiskState.Prepared)throw new IOException("The restored application could not be verified.");
    }
}
