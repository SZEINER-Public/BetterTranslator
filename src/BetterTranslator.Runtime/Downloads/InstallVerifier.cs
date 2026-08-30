using BetterTranslator.Runtime.Inference;
using BetterTranslator.Runtime.Models;

namespace BetterTranslator.Runtime.Downloads;

public sealed record InstallVerification(bool Passed, string Detail, IReadOnlyList<string> Missing)
{
    public static InstallVerification Ok(string detail) => new(true, detail, []);

    public static InstallVerification Fail(string detail, IReadOnlyList<string> missing) => new(false, detail, missing);
}

public interface IInstallVerifier
{
    Task<InstallVerification> VerifyAsync(ModelComponent component, CancellationToken cancellationToken);
}

public sealed class ComponentInstallVerifier(InstallPaths paths) : IInstallVerifier
{
    public Task<InstallVerification> VerifyAsync(ModelComponent component, CancellationToken cancellationToken)
    {
        var folder = paths.ModelsFolder;
        var missing = new List<string>();

        if (!ComponentInstallState.ArtifactMatches(paths.PathFor(component), component.SizeBytes))
        {
            missing.Add(component.FileName);
        }

        missing.AddRange(ComponentInstallState.MissingIn(folder, component).Select(c => c.FileName));

        if (missing.Count > 0)
        {
            return Task.FromResult(InstallVerification.Fail(
                $"{component.Name} was installed into {folder} but {string.Join(" and ", missing)} "
                + "is not there at the expected length.",
                missing));
        }

        // The folder the install actually wrote to, told to the loader here
        // rather than only at startup. Registered once at startup from the
        // default folder, the loader and every surface asking whether a flavour
        // is installed were blind to a folder the reader chose, so the runtime
        // card went on offering a component that was already on disk.
        if (component.Kind == ComponentKind.Runtime)
        {
            BackendCatalog.SearchAlso(folder);
        }

        return Task.FromResult(InstallVerification.Ok(
            $"{component.Name} verified in {folder}: "
            + $"{string.Join(", ", new[] { component.FileName }.Concat(component.Companions.Select(c => c.FileName)))}."));
    }
}
