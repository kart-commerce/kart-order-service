using Xunit;

// Each test class spins up its own OrderApiFactory (its own Postgres/Mongo/RabbitMQ containers)
// and sets the process-wide GlobalConfig__Path environment variable in InitializeAsync - running
// different test classes in parallel would race that shared, process-global environment variable
// across factories. Real-container integration tests are already slow relative to unit tests, so
// serializing across classes is an acceptable trade-off for correctness here.
[assembly: CollectionBehavior(DisableTestParallelization = true)]
