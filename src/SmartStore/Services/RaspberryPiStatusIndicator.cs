using System.Device.Gpio;
using Microsoft.Extensions.Options;
using SmartStore.Options;

namespace SmartStore.Services;

/// <summary>
/// Controls the two status LEDs and active buzzer attached to the Raspberry Pi.
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
            for (var beep = 0; beep < _options.FailureBeepCount; beep++)
            {
                _controller.Write(_options.BuzzerPin, PinValue.High);
                await Task.Delay(_options.FailureBeepMilliseconds, cancellationToken);
                _controller.Write(_options.BuzzerPin, PinValue.Low);

                if (beep + 1 < _options.FailureBeepCount)
                {
                    await Task.Delay(_options.FailureBeepMilliseconds, cancellationToken);
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
            // System.Device.Gpio uses logical/BCM numbering by default.
            _controller = new GpioController();
            _controller.OpenPin(_options.BlueLedPin, PinMode.Output, PinValue.Low);
            _controller.OpenPin(_options.RedLedPin, PinMode.Output, PinValue.Low);
            _controller.OpenPin(_options.BuzzerPin, PinMode.Output, PinValue.Low);

            _logger.LogInformation(
                "GPIO ready (blue BCM {BluePin}, red BCM {RedPin}, buzzer BCM {BuzzerPin}).",
                _options.BlueLedPin,
                _options.RedLedPin,
                _options.BuzzerPin);
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
            _controller.Write(_options.BuzzerPin, PinValue.Low);
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
