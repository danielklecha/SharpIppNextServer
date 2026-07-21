
namespace IppPrinter.Services
{
    public interface IDateTimeOffsetProvider
    {
        DateTimeOffset Now { get; }
        DateTimeOffset UtcNow { get; }
    }
}