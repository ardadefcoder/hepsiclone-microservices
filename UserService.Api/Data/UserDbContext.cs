using Microsoft.EntityFrameworkCore;
using UserService.Api.Models;

namespace UserService.Api.Data
{
    public class UserDbContext : DbContext
    {
        public UserDbContext(DbContextOptions<UserDbContext> options) : base(options) { }

        public DbSet<User> Users { get; set; }

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            // Username ve Email benzersiz olsun (aynı kullanıcı adı iki kez kaydedilemesin)
            modelBuilder.Entity<User>().HasIndex(u => u.Username).IsUnique();
            modelBuilder.Entity<User>().HasIndex(u => u.Email).IsUnique();
        }
    }
}