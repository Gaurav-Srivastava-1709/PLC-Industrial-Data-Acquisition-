namespace WTT.Domain.Entities;

/// <summary>
/// TransferStatus: Har data transfer batch ka status track karta hai.
/// Jaise courier parcel ka status hota hai — Pending, In Transit, Delivered, Failed.
/// </summary>
public enum TransferStatus
{
    /// <summary>Batch abhi queue me hai, transfer shuru nahi hua</summary>
    Pending = 0,

    /// <summary>Transfer chal raha hai abhi (in progress)</summary>
    InProgress = 1,

    /// <summary>Sab records successfully transfer ho gaye</summary>
    Success = 2,

    /// <summary>Transfer fail ho gaya — network error, SQL error, etc.</summary>
    Failed = 3,

    /// <summary>Kuch records transfer hue, kuch nahi — partial success</summary>
    PartialSuccess = 4
}
