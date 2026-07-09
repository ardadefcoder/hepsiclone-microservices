using BasketService.Api.Models;
using Microsoft.EntityFrameworkCore;

namespace BasketService.Api.Data
{
    public class BasketDbContext : DbContext
    {
        public BasketDbContext(DbContextOptions<BasketDbContext> options) : base(options) { }

        public DbSet<Basket> Baskets { get; set; }
        public DbSet<BasketItem> BasketItems { get; set; }
    }
}