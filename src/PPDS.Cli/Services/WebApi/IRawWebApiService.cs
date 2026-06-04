using PPDS.Cli.Infrastructure.Progress;

namespace PPDS.Cli.Services.WebApi;

public interface IRawWebApiService
{
    Task<RawWebApiResponse> SendAsync(
        RawWebApiRequest request,
        IProgressReporter? progress = null,
        CancellationToken cancellationToken = default);
}
