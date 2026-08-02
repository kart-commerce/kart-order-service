namespace KartOrderService.Application.Common.Models;

/// <summary>`api-contract.yaml`'s `Money` schema.</summary>
public sealed record MoneyDto(decimal Amount, string Currency);
