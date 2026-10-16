using Microsoft.Data.Sqlite;
using OnAirNext.Engine;
using Xunit;

namespace OnAirNext.Tests;

public sealed class JournalGroupCommitTests
{
    private static string Database() => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "RadioTEDU","OnAirNext-Dev","unit-tests",Guid.NewGuid().ToString("N"),"groups.db");
    private static void Sql(SqliteConnection connection,SqliteTransaction transaction,string text)
    {using var command=connection.CreateCommand();command.Transaction=transaction;command.CommandText=text;command.ExecuteNonQuery();}
    private static long MarkerCount(string path)
    {
        using var connection=new SqliteConnection(new SqliteConnectionStringBuilder{DataSource=path,Mode=SqliteOpenMode.ReadOnly,Pooling=false}.ToString());
        connection.Open();using var command=connection.CreateCommand();command.CommandText="SELECT COUNT(*) FROM batch_markers";return (long)command.ExecuteScalar()!;
    }
    private static Task Hold(EventJournal journal,TaskCompletionSource entered,ManualResetEventSlim release)
        => journal.TransactionAsync((_,_)=>{entered.TrySetResult();if(!release.Wait(TimeSpan.FromSeconds(5)))throw new TimeoutException("Fixture writer hold expired.");return true;});
    private static PlaybackEvent Start(string id)=>new("start-"+id,"attempt-"+id,"station","started","song-"+id,"Synthetic artist",id,
        ContentKind.Music,DateTimeOffset.UtcNow,0);

    [Fact]
    public async Task ConcurrentWritesShareFullCommitsAndAreNotAcknowledgedWhileStorageIsHeld()
    {
        var path=Database();
        await using(var journal=new EventJournal(path))
        {
            await journal.TransactionAsync((c,t)=>{Sql(c,t,"CREATE TABLE batch_markers(value TEXT NOT NULL)");return true;});
            var initial=journal.CommittedTransactions;
            var entered=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            using var release=new ManualResetEventSlim();var hold=Hold(journal,entered,release);
            Task<bool>[] pending=[];
            try
            {
                await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
                pending=Enumerable.Range(0,64).Select(_=>journal.TransactionAsync((c,t)=>{Sql(c,t,"INSERT INTO batch_markers VALUES('kept')");return true;})).ToArray();
                Assert.All(pending,p=>Assert.False(p.IsCompleted));Assert.Equal(64,journal.PendingEvents);
            }
            finally{release.Set();}
            await hold;await Task.WhenAll(pending);
            Assert.InRange(journal.CommittedTransactions-initial,2,3);
            Assert.Equal(64,MarkerCount(path));
        }
        await using var reopened=new EventJournal(path);Assert.Equal(64,MarkerCount(path));
    }

    [Fact]
    public async Task RejectedRequestRollsBackItsPartialWriteAndKeepsSiblingAttemptHistory()
    {
        var path=Database();await using var journal=new EventJournal(path);
        await journal.TransactionAsync((c,t)=>{Sql(c,t,"CREATE TABLE batch_markers(value TEXT NOT NULL)");return true;});
        var entered=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var release=new ManualResetEventSlim();var hold=Hold(journal,entered,release);
        Task? first=null,last=null;Task<int>? bad=null;
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            first=journal.AppendAsync(Start("first"));
            bad=journal.TransactionAsync<int>((c,t)=>{Sql(c,t,"INSERT INTO batch_markers VALUES('rejected')");throw new InvalidDataException("Synthetic rejected request");});
            last=journal.AppendAsync(Start("last"));
            Assert.False(first.IsCompleted);Assert.False(last.IsCompleted);Assert.False(bad.IsCompleted);
        }
        finally{release.Set();}
        await hold;await first!;await last!;await Assert.ThrowsAsync<InvalidDataException>(()=>bad!);
        Assert.Equal(0,MarkerCount(path));Assert.Equal(2,journal.CommittedEvents);
        Assert.Equal(new[]{"song-first","song-last"},EventJournal.ReadEvents(path).Select(e=>e.ItemId).ToArray());
        Assert.True(EventJournal.Verify(path));
    }

    [Fact]
    public async Task WholeTransactionRollbackCannotAcknowledgeEarlierRequestsAndWriterRecovers()
    {
        var path=Database();await using var journal=new EventJournal(path);
        await journal.TransactionAsync((c,t)=>
        {
            Sql(c,t,"CREATE TABLE batch_markers(value TEXT NOT NULL); CREATE TRIGGER force_abort BEFORE INSERT ON batch_markers WHEN NEW.value='abort' BEGIN SELECT RAISE(ROLLBACK,'synthetic whole transaction rollback'); END;");return true;
        });
        var entered=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var release=new ManualResetEventSlim();var hold=Hold(journal,entered,release);
        Task<bool>[] requests=[];
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            requests=new[]{"first","abort","last"}.Select(value=>journal.TransactionAsync((c,t)=>
            {using var command=c.CreateCommand();command.Transaction=t;command.CommandText="INSERT INTO batch_markers VALUES($value)";command.Parameters.AddWithValue("$value",value);command.ExecuteNonQuery();return true;})).ToArray();
            Assert.All(requests,r=>Assert.False(r.IsCompleted));
        }
        finally{release.Set();}
        await hold;foreach(var request in requests)await Assert.ThrowsAnyAsync<Exception>(()=>request);
        Assert.Equal(0,MarkerCount(path));
        Assert.True(await journal.TransactionAsync((c,t)=>{Sql(c,t,"INSERT INTO batch_markers VALUES('recovered')");return true;}));
        Assert.Equal(1,MarkerCount(path));Assert.Null(journal.Error);
    }
}
