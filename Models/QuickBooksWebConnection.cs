namespace AgrocultivoWebSync.Models;

public class QuickBooksWebConnection
{
    public int Id { get; set; }

    public string RealmId { get; set; } = string.Empty;

    public string AccessToken { get; set; } = string.Empty;

    public string RefreshToken { get; set; } = string.Empty;

    public DateTime AccessTokenExpiresAt { get; set; }

    public DateTime? RefreshTokenExpiresAt { get; set; }

    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}