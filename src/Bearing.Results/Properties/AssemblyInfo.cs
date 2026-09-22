using System.Runtime.CompilerServices;

// These types' tests live in Bearing.App.Tests, where they were written before the extraction;
// Bearing.Results.Tests is where new ones go.
[assembly: InternalsVisibleTo("Bearing.App.Tests")]
[assembly: InternalsVisibleTo("Bearing.Results.Tests")]
