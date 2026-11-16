using System.Resources;

// The neutral string resources are the fallback language: a culture without its satellite reads
// them from the main assembly instead of probing for a culture-specific assembly.
[assembly: NeutralResourcesLanguage("en", UltimateResourceFallbackLocation.MainAssembly)]
