namespace SubtitleTranslator.Application.Services;

public sealed class WorkerRuntimeStatus
{
    private int isOnline;

    public bool IsOnline => Volatile.Read(ref isOnline) == 1;

    public void MarkOnline() => Volatile.Write(ref isOnline, 1);

    public void MarkOffline() => Volatile.Write(ref isOnline, 0);
}