namespace DataAccess.Models;

/// <summary>
/// Represents a passenger entity stored in PostgreSQL.
/// </summary>
public class Passenger
{
    public string Id { get; set; } = string.Empty;
    public string FirstName { get; set; } = string.Empty;
    public string LastName { get; set; } = string.Empty;
    public string Email { get; set; } = string.Empty;
    public string PassportNumber { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

public record CreatePassengerDto(
    string? Id,
    string FirstName,
    string LastName,
    string Email,
    string PassportNumber
);

/// <summary>
/// Check-in record document stored in PostgreSQL.
/// </summary>
public class CheckinRecord
{
    public string Id { get; set; } = string.Empty;
    public string PassengerId { get; set; } = string.Empty;
    public string FlightNumber { get; set; } = string.Empty;
    public string SeatNumber { get; set; } = string.Empty;
    public int BaggageCount { get; set; }
    public string Status { get; set; } = "COMPLETED";
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

public record CreateCheckinDto(
    string? Id,
    string PassengerId,
    string FlightNumber,
    string SeatNumber,
    int BaggageCount,
    string? Status
);
