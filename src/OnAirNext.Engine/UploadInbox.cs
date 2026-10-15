using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text.Json;
using OnAirNext.UploadProtocol;

namespace OnAirNext.Engine;

public sealed record UploadProgress(string Id, string StationId, string State, int FileCount, int ReceivedFiles, long TotalBytes);
public sealed class UploadInbox
{
    public const long MaximumBytes = 240L * 1024 * 1024;
    private static readonly HashSet<string> Extensions = new(StringComparer.OrdinalIgnoreCase)
        { ".mp3", ".wav", ".flac", ".m4a", ".aac", ".ogg", ".opus", ".aif", ".aiff", ".wma", ".mp4" };
    private readonly EventJournal _journal;
    private readonly StationLibrary _library;
    private readonly string _root;
    private readonly ConcurrentDictionary<string,int> _received = new(StringComparer.Ordinal);
    public UploadInbox(EventJournal journal, StationLibrary library)
    { _journal=journal;_library=library;_root=Path.Combine(Path.GetDirectoryName(journal.DatabasePath)!,"upload-inbox"); }
    public async Task InitializeAsync()
    {
        RequireOwn(_root);Directory.CreateDirectory(_root);
        foreach(var receipt in _journal.ActiveLibraryUploads())
        {
            var files=Files(receipt);var count=0;
            for(var i=0;i<files.Length;i++){var path=FilePath(receipt,i);if(File.Exists(path)&&new FileInfo(path).Length==files[i].Bytes)count++;}
            _received[receipt.Id]=count;
        }
        await CleanupCompletedAsync();
    }
    public UploadProgress[] Snapshot(string station) => _journal.ActiveLibraryUploads().Where(x=>x.StationId==station).Select(receipt=>
    {
        var files=Files(receipt);return new UploadProgress(receipt.Id,station,_journal.LibraryImportState(receipt.Id)??receipt.State,
            files.Length,_received.GetValueOrDefault(receipt.Id),files.Sum(file=>file.Bytes));
    }).ToArray();
    public async Task<LibraryUpload> BeginAsync(string station,string batch,UploadFileSpec[] files)
    {
        batch=Id(batch);files=Validate(files);var payload=JsonSerializer.Serialize(files);
        await CleanupCompletedAsync();
        var previous=_journal.LibraryUpload(batch);
        if(previous is null)
        {
            if(!_library.CanImportFiles(_library.Page(station,"",0).Root))
                throw new InvalidOperationException("External live folders are read-only. Choose managed storage before uploading files.");
            var reserved=_journal.ActiveLibraryUploads().Sum(receipt=>Files(receipt).Sum(file=>file.Bytes));
            if(new DriveInfo(Path.GetPathRoot(_root)!).AvailableFreeSpace < reserved+files.Sum(file=>file.Bytes)+2L*1024*1024*1024)
                throw new IOException("The engine upload drive needs at least 2 GiB of space after retained uploads.");
        }
        var result=await _journal.RegisterLibraryUploadAsync(station,batch,payload);
        if((result.State is "Cancelled" or "Cancelling") && _journal.LibraryImportState(batch) is null)
            throw new InvalidOperationException("This transfer was cancelled. Start a new upload.");
        return result;
    }
    public async Task<bool> HasFileAsync(string station,string batch,int index,CancellationToken token)
    {
        var receipt=Require(station,batch);var files=Files(receipt);
        if(index<0||index>=files.Length)throw new ArgumentException("Invalid upload file index.");
        var path=FilePath(receipt,index);if(!File.Exists(path))return false;
        await VerifyAsync(path,files[index],token);UpdateReceived(receipt);return true;
    }
    public async Task ReceiveAsync(string station,string batch,int index,Stream input,CancellationToken token)
    {
        var receipt=Require(station,batch);if(receipt.State!="Receiving")throw new InvalidOperationException("This upload is no longer receiving files.");
        var files=Files(receipt);if(index<0||index>=files.Length)throw new ArgumentException("Invalid upload file index.");var spec=files[index];
        var destination=FilePath(receipt,index);var directory=Path.GetDirectoryName(destination)!;RequireOwn(directory);Directory.CreateDirectory(directory);
        var partial=Path.Combine(directory,"transfer.partial");RequireOwn(partial);
        try
        {
            using var hash=IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            await using(var output=new FileStream(partial,FileMode.Create,FileAccess.Write,FileShare.None,65536,true))
            {
                var remaining=spec.Bytes;var buffer=new byte[65536];
                while(remaining>0)
                {
                    var read=await input.ReadAsync(buffer.AsMemory(0,(int)Math.Min(buffer.Length,remaining)),token);
                    if(read==0)throw new EndOfStreamException("The upload ended before the declared file length.");
                    await output.WriteAsync(buffer.AsMemory(0,read),token);hash.AppendData(buffer,0,read);remaining-=read;
                }
                await output.FlushAsync(token);output.Flush(flushToDisk:true);
                if(Convert.ToHexString(hash.GetHashAndReset())!=spec.Sha256)throw new IOException("The uploaded bytes do not match the selected file.");
            }
            if(File.Exists(destination))await VerifyAsync(destination,spec,token);
            else {RequireOwn(destination);File.Move(partial,destination,false);}
            UpdateReceived(receipt);
        }
        finally {RequireOwn(partial);if(File.Exists(partial))File.Delete(partial);}
    }
    public async Task CommitAsync(string station,string batch,CancellationToken token)
    {
        var receipt=Require(station,batch);var files=Files(receipt);var paths=Enumerable.Range(0,files.Length).Select(i=>FilePath(receipt,i)).ToArray();
        // A lost acknowledgement may be retried after successful staged-copy cleanup.
        if(_journal.LibraryImportState(receipt.Id) is null)
        {
            if(receipt.State!="Receiving")throw new InvalidOperationException("The upload is not available for import.");
            for(var i=0;i<files.Length;i++)await VerifyAsync(paths[i],files[i],token);
        }
        await _library.ImportAsync(station,paths,receipt.Id,uploaded:true);
    }
    public async Task CancelAsync(string station,string batch)
    {
        var receipt=Require(station,batch);
        await _journal.FinishLibraryUploadAsync(receipt.Id,station,cancel:true);
        DeleteStagedCopies(receipt);
        await _journal.FinishLibraryUploadAsync(receipt.Id,station,cancel:true,filesRemoved:true);_received.TryRemove(receipt.Id,out _);
    }
    private async Task CleanupCompletedAsync()
    {
        foreach(var receipt in _journal.ActiveLibraryUploads())
        {
            if(receipt.State=="Cancelling") {await CancelAsync(receipt.StationId,receipt.Id);continue;}
            if(_journal.LibraryImportState(receipt.Id)!="Completed")continue;
            DeleteStagedCopies(receipt);await _journal.FinishLibraryUploadAsync(receipt.Id,receipt.StationId,cancel:false,filesRemoved:true);
            _received.TryRemove(receipt.Id,out _);
        }
    }
    private void DeleteStagedCopies(LibraryUpload receipt)
    {
        var files=Files(receipt);
        for(var i=0;i<files.Length;i++)
        {
            var destination=FilePath(receipt,i);var directory=Path.GetDirectoryName(destination)!;var partial=Path.Combine(directory,"transfer.partial");
            foreach(var file in new[]{destination,partial}){RequireOwn(file);if(File.Exists(file))File.Delete(file);}
            RequireOwn(directory);if(Directory.Exists(directory))Directory.Delete(directory,false);
        }
        var root=Path.Combine(_root,receipt.Id);RequireOwn(root);if(Directory.Exists(root))Directory.Delete(root,false);
    }
    private LibraryUpload Require(string station,string batch)
    {
        var receipt=_journal.LibraryUpload(Id(batch))??throw new InvalidOperationException("Upload is unavailable.");
        if(receipt.StationId!=station)throw new InvalidOperationException("Upload belongs to another station.");return receipt;
    }
    private static string Id(string value)=>Guid.TryParse(value,out var id)?id.ToString("N"):throw new ArgumentException("Invalid upload ID.");
    private static UploadFileSpec[] Files(LibraryUpload receipt)=>JsonSerializer.Deserialize<UploadFileSpec[]>(receipt.Payload)
        ??throw new InvalidDataException("Upload manifest is unreadable.");
    private string FilePath(LibraryUpload receipt,int index)
    {
        var path=Path.Combine(_root,receipt.Id,index.ToString("D2",System.Globalization.CultureInfo.InvariantCulture),Files(receipt)[index].Name);
        RequireOwn(path);return path;
    }
    private void UpdateReceived(LibraryUpload receipt)
    {
        var files=Files(receipt);_received[receipt.Id]=Enumerable.Range(0,files.Length).Count(i=>File.Exists(FilePath(receipt,i)));
    }
    private static async Task VerifyAsync(string path,UploadFileSpec spec,CancellationToken token)
    {
        await using var file=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.Read,65536,true);
        if(file.Length!=spec.Bytes||Convert.ToHexString(await SHA256.HashDataAsync(file,token))!=spec.Sha256)
            throw new IOException("The retained upload is incomplete or changed. Cancel it and select the original files again.");
    }
    private void RequireOwn(string path)
    {
        var full=Path.GetFullPath(path);var root=Path.GetFullPath(_root);
        var comparison=OperatingSystem.IsWindows()?StringComparison.OrdinalIgnoreCase:StringComparison.Ordinal;
        if(!full.Equals(root,comparison)&&!full.StartsWith(root+Path.DirectorySeparatorChar,comparison))throw new IOException("Upload path escaped private storage.");
        for(var item=full;!string.IsNullOrEmpty(item);item=Path.GetDirectoryName(item))
        {
            try {if((File.GetAttributes(item)&FileAttributes.ReparsePoint)!=0)throw new IOException("Upload storage cannot traverse links.");}
            catch(FileNotFoundException){}catch(DirectoryNotFoundException){}
        }
    }
    private static UploadFileSpec[] Validate(UploadFileSpec[] files)
    {
        if(files is null || files.Length is <1 or >64)throw new ArgumentException("Choose between 1 and 64 audio files.");long total=0;
        var normalized=new List<UploadFileSpec>();
        foreach(var file in files)
        {
            if(file is null || file.Name is null || file.Sha256 is null)throw new ArgumentException("Upload identities are required.");
            var name=file.Name;var stem=name.Split('.')[0].ToUpperInvariant();
            if(name.Length is <1 or >160||name is "." or ".."||name.TrimEnd(' ','.')!=name||name.IndexOfAny(Path.GetInvalidFileNameChars())>=0||name.Contains('/')||name.Contains('\\')||name.Contains(':')||
                stem is "CON" or "PRN" or "AUX" or "NUL"||stem.Length==4&&(stem.StartsWith("COM",StringComparison.Ordinal)||stem.StartsWith("LPT",StringComparison.Ordinal))&&stem[3] is >= '1' and <= '9'||!Extensions.Contains(Path.GetExtension(name))||file.Bytes<=0||file.Bytes>MaximumBytes||
                file.Sha256.Length!=64||!file.Sha256.All(Uri.IsHexDigit))throw new ArgumentException("Choose valid audio filenames, lengths and SHA-256 identities.");
            total=checked(total+file.Bytes);if(total>MaximumBytes)throw new ArgumentException("Use upload batches no larger than 240 MiB.");
            normalized.Add(file with {Sha256=file.Sha256.ToUpperInvariant()});
        }
        return normalized.ToArray();
    }
}
