using CafePos.Core.Data;
using CafePos.Core.Errors;
using CafePos.Core.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace CafePos.Core.Services;

public sealed partial class CatalogService(
    IDbContextFactory<AppDbContext> factory,
    TimeProvider timeProvider,
    ILogger<CatalogService> logger) : ICatalogService
{

    // The rest of the class lives in partials named after the aggregate they serve:
    // Reading, Categories, Products, Modifiers, Ingredients.
}
