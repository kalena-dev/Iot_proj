using System.Device.Gpio;
using System.Diagnostics;
using System.Device.Gpio.Drivers;
using Microsoft.Extensions.Options;
using SmartStore.Options;

namespace SmartStore.Services;

/// <summary>
/// Controls the two status LEDs and active or passive buzzer attached to the Raspberry Pi.
/// Outside Linux, or when GPIO cannot be opened, signals are logged so the web
/// application remains usable during development.
/// </summary>
public sealed class RaspberryPiStatusIndicator : IStatusIndicator, IDisposable
{
    private readonly GpioOptions _options;
    private readonly ILogger<RaspberryPiStatusIndicator> _logger;
    private readonly SemaphoreSlim _signalLock = new(1, 1);
    private GpioController? _controller;
    private bool _initializationAttempted;
    private bool _disposed;

    public RaspberryPiStatusIndicator(
        IOptions<GpioOptions> options,
        ILogger<RaspberryPiStatusIndicator> logger)
    {
        _options = options.Value;
        _logger = logger;
    }

    public Task ShowSuccessAsync(CancellationToken cancellationToken = default) =>
        RunSignalAsync(success: true, cancellationToken);

    public Task ShowFailureAsync(CancellationToken cancellationToken = default) =>
        RunSignalAsync(success: false, cancellationToken);

    private async Task RunSignalAsync(bool success, CancellationToken cancellationToken)
    {
        await _signalLock.WaitAsync(cancellationToken);
        try
        {
            if (!TryInitializeController())
            {
                _logger.LogInformation(
                    "GPIO simulation: {Signal} signal requested.",
                    success ? "success" : "failure");
                return;
            }

            AllOff();

            if (success)
            {
                _controller!.Write(_options.BlueLedPin, PinValue.High);
                await Task.Delay(_options.SuccessDurationMilliseconds, cancellationToken);
                return;
            }

            _controller!.Write(_options.RedLedPin, PinValue.High);
            var beepCount = Math.Clamp(_options.FailureBeepCount, 1, 5);
            var beepMilliseconds = Math.Clamp(_options.FailureBeepMilliseconds, 100, 1000);
            if (!_options.BuzzerEnabled)
            {
                await Task.Delay(beepMilliseconds, cancellationToken);
            }
            for (var beep = 0; _options.BuzzerEnabled && beep < beepCount; beep++)
            {
                if (_options.BuzzerMode == BuzzerMode.Passive)
                {
                    // GPIO 22 has no hardware PWM on Pi 5. Generate a short
                    // 50%-duty-cycle tone on a worker thread instead.
                    await Task.Run(() => PlayPassiveTone(beepMilliseconds, cancellationToken), cancellationToken);
                }
                else
                {
                    _controller.Write(_options.BuzzerPin, PinValue.High);
                    await Task.Delay(beepMilliseconds, cancellationToken);
                }

                _controller.Write(_options.BuzzerPin, PinValue.Low);
                if (beep + 1 < beepCount)
                {
                    await Task.Delay(180, cancellationToken);
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // The finally block still returns every output to a safe low state.
        }
        catch (Exception exception)
        {
            // A GPIO problem must not turn a successful database write into a
            // failed customer registration.
            _logger.LogError(exception, "Unable to operate the GPIO status indicators.");
            DisposeController();
        }
        finally
        {
            AllOff();
            _signalLock.Release();
        }
    }

    private void PlayPassiveTone(int durationMilliseconds, CancellationToken cancellationToken)
    {
        var frequencyHz = Math.Clamp(_options.BuzzerFrequencyHz, 500, 4000);
        var halfPeriodTicks = Math.Max(1, Stopwatch.Frequency / (2 * frequencyHz));
        var end = Stopwatch.GetTimestamp() + Stopwatch.Frequency * durationMilliseconds / 1000;
        var nextToggle = Stopwatch.GetTimestamp();
        var high = false;

        while (Stopwatch.GetTimestamp() < end)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _controller!.Write(_options.BuzzerPin, high ? PinValue.Low : PinValue.High);
            high = !high;
            nextToggle += halfPeriodTicks;
            while (Stopwatch.GetTimestamp() < nextToggle && Stopwatch.GetTimestamp() < end)
            {
                cancellationToken.ThrowIfCancellationRequested();
                Thread.SpinWait(20);
            }

            // Do not try to catch up by issuing GPIO writes in a burst if
            // Linux pauses this thread (that would distort the pitch).
            nextToggle = Math.Max(nextToggle, Stopwatch.GetTimestamp());
        }

        _controller!.Write(_options.BuzzerPin, PinValue.Low);
    }

    private bool TryInitializeController()
    {
        if (_controller is not null)
        {
            return true;
        }

        if (_initializationAttempted || !_options.Enabled || !OperatingSystem.IsLinux())
        {
            return false;
        }

        _initializationAttempted = true;

        try
        {
            // System.Device.Gpio 4.2 defaults to libgpiod v1 on Pi 5. Debian 13
            // ships only libgpiod.so.3 (v2 ABI), so select its RP1 driver explicitly.
            _controller = CreateController();
            _controller.OpenPin(_options.BlueLedPin, PinMode.Output, PinValue.Low);
            _controller.OpenPin(_options.RedLedPin, PinMode.Output, PinValue.Low);
            if (_options.BuzzerEnabled)
            {
                _controller.OpenPin(_options.BuzzerPin, PinMode.Output, PinValue.Low);
            }

            _logger.LogInformation(
                "GPIO ready (blue BCM {BluePin}, red BCM {RedPin}, buzzer {BuzzerState}).",
                _options.BlueLedPin,
                _options.RedLedPin,
                _options.BuzzerEnabled ? $"BCM {_options.BuzzerPin}" : "disabled");
            return true;
        }
        catch (Exception exception)
        {
            _logger.LogWarning(
                exception,
                "GPIO is unavailable. Status signals will be logged instead. Check wiring and /dev/gpiochip permissions.");
            DisposeController();
            return false;
        }
    }

    private static GpioController CreateController()
    {
        if (OperatingSystem.IsLinux() &&
            File.Exists("/proc/device-tree/model") &&
            File.ReadAllText("/proc/device-tree/model").Contains("Raspberry Pi 5", StringComparison.Ordinal))
        {
            try
            {
                var rp1 = LibGpiodV2Driver.GetAvailableChips()
                    .FirstOrDefault(chip => chip.NumLines == 54 && chip.Label.Contains("rp1", StringComparison.OrdinalIgnoreCase));
                if (rp1 is not null)
                {
                    return new GpioController(new LibGpiodV2Driver(rp1));
                }
            }
            catch (DllNotFoundException)
            {
                // Older Pi OS versions have only libgpiod v1; use the package default.
            }
        }

        return new GpioController();
    }

    private void AllOff()
    {
        if (_controller is null)
        {
            return;
        }

        try
        {
            _controller.Write(_options.BlueLedPin, PinValue.Low);
            _controller.Write(_options.RedLedPin, PinValue.Low);
            if (_options.BuzzerEnabled)
            {
                _controller.Write(_options.BuzzerPin, PinValue.Low);
            }
        }
        catch (Exception exception)
        {
            _logger.LogDebug(exception, "Could not reset one or more GPIO outputs.");
        }
    }

    private void DisposeController()
    {
        try
        {
            _controller?.Dispose();
        }
        catch (Exception exception)
        {
            _logger.LogDebug(exception, "Could not dispose the GPIO controller cleanly.");
        }
        finally
        {
            _controller = null;
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        AllOff();
        DisposeController();
        _signalLock.Dispose();
    }
}
