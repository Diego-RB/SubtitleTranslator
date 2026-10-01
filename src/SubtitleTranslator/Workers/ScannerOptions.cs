namespace SubtitleTranslator.Workers;

public sealed class ScannerOptions
{
    public bool Enabled { get; set; } = true;
    public int IntervalSeconds { get; set; } = 30;
    public string[] Directories { get; set; } = [];
    public int RecoveryIntervalSeconds { get; set; } = 60;
    public int StalledJobThresholdMinutes { get; set; } = 10;
    public int HeartbeatIntervalSeconds { get; set; } = 30;
}
