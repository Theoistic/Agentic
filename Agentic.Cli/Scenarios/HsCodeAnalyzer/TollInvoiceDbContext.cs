using Microsoft.EntityFrameworkCore;

namespace Agentic.Cli;

public class TollInvoiceDbContext : DbContext
{
    public DbSet<TollInvoice>   Invoices  => Set<TollInvoice>();
    public DbSet<TollLineItem>  LineItems => Set<TollLineItem>();

    protected override void OnConfiguring(DbContextOptionsBuilder opt) =>
        opt.UseInMemoryDatabase("toll_invoices");

    protected override void OnModelCreating(ModelBuilder mb)
    {
        mb.Entity<TollInvoice>()
          .HasMany(i => i.LineItems)
          .WithOne(l => l.Invoice)
          .HasForeignKey(l => l.TollInvoiceId);
    }
}
