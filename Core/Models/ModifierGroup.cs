namespace CafePos.Core.Models;

public class ModifierGroup
{
    public Guid Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public List<ModifierOption> Options { get; set; } = new();
    public List<Product> Products { get; set; } = new();
}
