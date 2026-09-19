using Xunit;

// Every UI test shares one WPF Application instance (a process can only host one), and the theme
// audit changes the application theme. Running test classes in parallel therefore made results
// depend on timing: the audit could read the palette while another class was laying out a page
// under a different theme. The suite is fast enough to run one class at a time.
[assembly: CollectionBehavior(DisableTestParallelization = true)]
