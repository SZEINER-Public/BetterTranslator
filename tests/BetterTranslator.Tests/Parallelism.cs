using Xunit;

// WPF's Application is process-wide. The preview probe has to merge the theme
// dictionaries into Application.Current.Resources to lay a real view out, and
// while they are merged Tokens.TryMilliseconds answers for keys it otherwise
// reports as absent. Restoring them afterwards closes the leak for whatever runs
// next; only serialising closes it for whatever would have run alongside. The
// suite is a few seconds either way.
[assembly: CollectionBehavior(DisableTestParallelization = true)]
