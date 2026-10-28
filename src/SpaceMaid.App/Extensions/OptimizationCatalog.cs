using System.IO;
using SpaceMaid.Extensions.Contracts;

namespace SpaceMaid.App.Extensions;

internal sealed record OptimizationContribution(string OwnerId, string OwnerName, ExtensionOptimizationAction Action);
internal sealed record OptimizationDiscovery(IReadOnlyList<OptimizationContribution> Actions, IReadOnlyList<string> Errors);

internal static class OptimizationCatalog
{
    internal static OptimizationDiscovery Discover(IEnumerable<ISpaceMaidExtension> extensions)
    {
        var actions = new List<OptimizationContribution>(); var errors = new List<string>();
        foreach (var extension in extensions.OfType<IOptimizationExtension>())
        {
            ExtensionDescriptor? owner = null;
            try
            {
                owner = ((ISpaceMaidExtension)extension).Descriptor ?? throw new InvalidDataException("Missing extension descriptor.");
                var declared = extension.GetOptimizationActions();
                if (declared is null || declared.Count > 32) throw new InvalidDataException("Too many optimization actions.");
                var ids = new HashSet<string>(StringComparer.Ordinal);
                foreach (var action in declared)
                {
                    if (action is null || !ValidId(action.Id) || !ids.Add(action.Id)
                        || !Text(action.Name, 128) || !Text(action.Description, 1024) || !Text(action.Consequence, 2048)
                        || (action.NativeTool is null) == (action.Workflow is null)
                        || action.NativeTool is { } tool && !Enum.IsDefined(tool)
                        || action.Workflow is { } workflow && !ValidId(workflow))
                        throw new InvalidDataException("Invalid optimization action declaration.");
                }
                actions.AddRange(declared.Select(action => new OptimizationContribution(owner.Id, owner.Name, action)));
            }
            catch (Exception error)
            { errors.Add((owner?.Name ?? extension.GetType().Name) + ": " + error.GetBaseException().Message); }
        }
        return new(actions.AsReadOnly(), errors.AsReadOnly());
    }
    private static bool ValidId(string value) => Text(value, 128) && value.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '.');
    private static bool Text(string? value, int limit) => !string.IsNullOrWhiteSpace(value) && value.Length <= limit;
}
