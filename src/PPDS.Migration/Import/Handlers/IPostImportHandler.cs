using System.Threading;
using System.Threading.Tasks;

namespace PPDS.Migration.Import.Handlers
{
    /// <summary>
    /// Executes post-import processing for an entity.
    /// </summary>
    public interface IPostImportHandler
    {
        /// <summary>
        /// Determines whether this handler applies to the given entity.
        /// </summary>
        /// <param name="entityLogicalName">The entity logical name.</param>
        /// <returns>True if this handler handles the entity.</returns>
        bool CanHandle(string entityLogicalName);

        /// <summary>
        /// Executes post-import processing for the given entity.
        /// </summary>
        /// <param name="entityLogicalName">The entity logical name.</param>
        /// <param name="context">The import context.</param>
        /// <param name="cancellationToken">The cancellation token.</param>
        /// <returns>A task representing the asynchronous operation.</returns>
        Task ExecuteAsync(string entityLogicalName, ImportContext context, CancellationToken cancellationToken);
    }
}
