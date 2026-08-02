using KartOrderService.Infrastructure.Persistence.ReadModel.Documents;
using MongoDB.Driver;

namespace KartOrderService.Infrastructure.Persistence.ReadModel;

/// <summary>The CQRS read side — a thin typed-collection accessor over the Mongo database, mirroring `kart-payment-service`'s `PaymentReadDbContext`.</summary>
public sealed class OrderReadDbContext(IMongoDatabase database)
{
    public IMongoCollection<OrderReadDocument> Orders => database.GetCollection<OrderReadDocument>("order_read_model");
}
