namespace KartOrderService.OrderSeeder;

/// <summary>Parsed CLI arguments for a single seeding run.</summary>
public sealed class SeedOptions
{
    public required int Count { get; init; }
    public int BatchSize { get; init; } = 500;
    public int? RandomSeed { get; init; }
    public bool EmitEvents { get; init; }
    public string? ConnectionString { get; init; }
    public string ActingPrincipal { get; init; } = "system:order-seeder";

    public static SeedOptions Parse(string[] args)
    {
        if (args.Length == 0 || args is ["-h" or "--help"])
        {
            throw new ArgUsageException(Usage);
        }

        if (!int.TryParse(args[0], out var count) || count <= 0)
        {
            throw new ArgUsageException($"<count> must be a positive integer, got '{args[0]}'.\n\n{Usage}");
        }

        var batchSize = 500;
        bool emitEvents = false;
        int? seed = null;
        string? connectionString = null;
        var principal = "system:order-seeder";

        for (var i = 1; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--batch-size":
                    batchSize = int.Parse(RequireValue(args, ref i, "--batch-size"));
                    break;
                case "--emit-events":
                    emitEvents = true;
                    break;
                case "--seed":
                    seed = int.Parse(RequireValue(args, ref i, "--seed"));
                    break;
                case "--connection":
                    connectionString = RequireValue(args, ref i, "--connection");
                    break;
                case "--principal":
                    principal = RequireValue(args, ref i, "--principal");
                    break;
                default:
                    throw new ArgUsageException($"Unknown option '{args[i]}'.\n\n{Usage}");
            }
        }

        if (batchSize <= 0)
        {
            throw new ArgUsageException("--batch-size must be a positive integer.");
        }

        return new SeedOptions
        {
            Count = count,
            BatchSize = batchSize,
            EmitEvents = emitEvents,
            RandomSeed = seed,
            ConnectionString = connectionString,
            ActingPrincipal = principal,
        };
    }

    private static string RequireValue(string[] args, ref int i, string option)
    {
        if (i + 1 >= args.Length)
        {
            throw new ArgUsageException($"'{option}' requires a value.");
        }

        return args[++i];
    }

    private const string Usage = """
        Usage: order-seeder <count> [options]

          <count>              How many orders to create in total.

        Options:
          --batch-size <n>     Rows per DB round-trip (default: 500).
          --emit-events        Leave the initial OrderCreated outbox/projection row unpublished/
                                unprojected, so the normal Outbox poller and read-model projector
                                pick up seeded orders too (default: off - seed runs don't spam
                                RabbitMQ/Mongo unless you ask for it; rows are stamped as already
                                published/projected instead).
          --seed <n>           Deterministic RNG seed, for reproducible fake data.
          --connection <str>   Postgres connection string (default: $ORDER_DB_CONNECTION_STRING,
                                falling back to the same localhost default OrderDbContextFactory uses).
          --principal <str>    created_by/updated_by value stamped on seeded rows
                                (default: system:order-seeder).

        Examples:
          order-seeder 500
          order-seeder 100000 --batch-size 2000 --seed 42
        """;
}

public sealed class ArgUsageException(string message) : Exception(message);
