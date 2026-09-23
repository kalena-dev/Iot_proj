namespace SmartStore.Services;

public interface IStatusIndicator
{
    Task ShowSuccessAsync(CancellationToken cancellationToken = default);

    Task ShowFailureAsync(CancellationToken cancellationToken = default);
}
