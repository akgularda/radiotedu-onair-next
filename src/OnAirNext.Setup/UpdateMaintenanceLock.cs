namespace OnAirNext.Installation;

// The elevated helper writes under this lease. The browser only probes with read
// access; the retained file is never created, removed or rewritten by a status read.
public static class UpdateMaintenanceLock
{
    public static bool IsHeld(string path)
    {
        SetupPaths.RequireNoLinks(path);
        try{using var probe=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.None);return false;}
        catch(IOException error)when((error.HResult&0xffff) is 32 or 33){return true;}
    }
    public static async Task<FileStream> AcquireAsync(string path,CancellationToken token=default)
    {
        var clock=System.Diagnostics.Stopwatch.StartNew();
        while(true)
        {
            token.ThrowIfCancellationRequested();SetupPaths.RequireNoLinks(path);
            try{return new FileStream(path,FileMode.Open,FileAccess.ReadWrite,FileShare.None);}
            catch(IOException error)when(((error.HResult&0xffff) is 32 or 33)&&clock.Elapsed<TimeSpan.FromSeconds(5))
            {await Task.Delay(20,token);}
        }
    }
}
