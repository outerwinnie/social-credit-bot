using System;

using CsvHelper.Configuration.Attributes;

public class VoteRecord
{
    [Index(0)]
    public ulong VoterId { get; set; }
    [Index(1)]
    public ulong VotedForId { get; set; }
    [Index(2)]
    public int BetAmount { get; set; }
    [Index(3)]
    public DateTime Timestamp { get; set; }
}
