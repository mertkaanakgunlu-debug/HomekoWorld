namespace HomekoWorld.Models;

public class Profile
{
    public string Id   { get; set; } = Guid.NewGuid().ToString();
    public string Name { get; set; } = string.Empty;
}
