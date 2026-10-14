namespace SmartStore.Options;

public enum BuzzerMode
{
    Active,
    Passive
}

public sealed class GpioOptions
{
    public const string SectionName = "Gpio";

    public bool Enabled { get; set; } = true;

    // BCM (logical) GPIO numbers, not physical header pin numbers.
    public int BlueLedPin { get; set; } = 17;

    public int RedLedPin { get; set; } = 27;

    public int BuzzerPin { get; set; } = 22;

    public bool BuzzerEnabled { get; set; } = true;

    public int SuccessDurationMilliseconds { get; set; } = 1500;

    public int FailureBeepCount { get; set; } = 3;

    public int FailureBeepMilliseconds { get; set; } = 450;

    // The kit's passive buzzer needs a tone; active buzzers use a steady high signal.
    public BuzzerMode BuzzerMode { get; set; } = BuzzerMode.Passive;

    public int BuzzerFrequencyHz { get; set; } = 2000;
}
