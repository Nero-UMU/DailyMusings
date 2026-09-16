using Microsoft.Extensions.DependencyInjection;

namespace DailyMusings.Client;

public partial class App : Application
{
	private readonly IServiceProvider _services;

	public App(IServiceProvider services)
	{
		InitializeComponent();

		_services = services;
	}

	protected override Window CreateWindow(IActivationState? activationState) =>
		new(_services.GetRequiredService<AppShell>());
}
