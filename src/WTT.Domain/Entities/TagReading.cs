using System;

namespace WTT.Domain.Entities;

/// <summary>
/// TagReading: Data Capture Service dwara har 10 second me read ki gayi historical value.
/// Industry standard ke hisaab se 30 days retention policy apply hogi.
/// </summary>
public class TagReading
{
    public long Id { get; set; }

    // Foreign key to DeviceTag
    public int DeviceTagId { get; set; }
    public DeviceTag DeviceTag { get; set; } = null!;

    // String form me capture ki gayi value (e.g. "72.45", "TRUE", "1250")
    public string Value { get; set; } = string.Empty;

    // Quality flag: "Good" (agar success), "Bad" (agar communication error), "Unknown"
    public string Quality { get; set; } = "Good";

    // Reading capture hone ka exact UTC timestamp
    public DateTime Timestamp { get; set; } = DateTime.UtcNow;

    // Central Server (Phase 4) ko transfer hua ya nahi
    public bool IsTransferred { get; set; } = false;
}
