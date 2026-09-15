namespace Wrapp.Services;

/// <summary>
/// <para>
/// Instance-facing logging surface. Mirrors the static <see cref="AppLogger"/>
/// API one-for-one so services / view-models that take an <c>IAppLogger</c>
/// via ctor get the same behaviour as those that still call
/// <c>AppLogger.Info</c> directly.
/// </para>
/// <para>
/// This interface exists to unblock test doubles: in production code, the
/// default implementation (<see cref="DefaultAppLogger"/>) delegates to the
/// static logger, so log rotation / redaction / correlation stay centralised.
/// In tests, a fake can capture entries into a list for assertion without
/// touching the file system or the async writer loop.
/// </para>
/// <para>
/// Migration note: new services should take <c>IAppLogger</c> via
/// constructor with <see cref="AppLogger.Instance"/> as the default argument.
/// Existing services can continue to call <c>AppLogger.Info</c> statically
/// until they're touched by a later refactor - both code paths write to the
/// same sink.
/// </para>
/// </summary>
public interface IAppLogger
{
    void Info(string message);
    void Warn(string message);
    void Error(string message);
    void Exception(string context, Exception ex);
}

/// <summary>
/// Production <see cref="IAppLogger"/> - forwards every call to the
/// corresponding <see cref="AppLogger"/> static. A single shared instance
/// is exposed via <see cref="AppLogger.Instance"/> so callers don't need to
/// allocate one per dependency.
/// </summary>
internal sealed class DefaultAppLogger : IAppLogger
{
    public void Info(string message)                  => AppLogger.Info(message);
    public void Warn(string message)                  => AppLogger.Warn(message);
    public void Error(string message)                 => AppLogger.Error(message);
    public void Exception(string context, Exception e) => AppLogger.Exception(context, e);
}
