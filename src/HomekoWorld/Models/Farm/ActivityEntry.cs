namespace HomekoWorld.Models.Farm;

/// <summary>
/// Log HUD'da gösterilen tek bir canlı aktivite kaydı.
/// Kind: "event" (durum mesajı) | "key" (gönderilen tuş) | "combo" (kombo adımı).
/// </summary>
public sealed record ActivityEntry(string Time, string Text, string Kind);
