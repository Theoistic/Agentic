namespace Agentic;

// ═══════════════════════════════════════════════════════════════════════════
//  DI registration
// ═══════════════════════════════════════════════════════════════════════════

/// <summary>Options for configuring the <see cref="IStore"/> registered by <see cref="StoreExtensions.AddStore"/>.</summary>
public sealed class StoreOptions
{
    /// <summary><c>sqlite</c> (default) or <c>postgres</c>.</summary>
    public string Provider { get; set; } = "sqlite";
    public string ConnectionString { get; set; } = "Data Source=agentic.db";
}

public static class StoreExtensions
{
    /// <summary>
    /// Register the appropriate <see cref="IStore"/> singleton based on the
    /// <c>Database:Provider</c> config key (<c>sqlite</c> or <c>postgres</c>).
    /// Connection string is read from <c>Database:ConnectionString</c>.
    /// </summary>
    public static IServiceCollection AddStore(
        this IServiceCollection services, IConfiguration? config = null)
    {
        var opts = new StoreOptions();
        config?.GetSection("Database").Bind(opts);

        IStore store = opts.Provider.Equals("postgres", StringComparison.OrdinalIgnoreCase) ||
                       opts.Provider.Equals("postgresql", StringComparison.OrdinalIgnoreCase)
            ? new PostgresStore(opts.ConnectionString)
            : new SqliteStore(opts.ConnectionString);

        services.AddSingleton<IStore>(store);
        return services;
    }

    /// <summary>Register an <see cref="InMemoryStore"/> as the <see cref="IStore"/> singleton.</summary>
    public static IServiceCollection AddInMemoryStore(this IServiceCollection services)
    {
        services.AddSingleton<IStore>(new InMemoryStore());
        return services;
    }
}
