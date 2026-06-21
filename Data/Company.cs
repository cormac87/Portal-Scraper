using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using NetTopologySuite.Geometries;

namespace PortalScraper.Data;

public sealed class Company
{
    public Guid Id { get; set; }

    [StringLength(512)]
    public string? CompanyName { get; set; }

    [Required]
    [StringLength(20)]
    public string CompanyNumber { get; set; } = string.Empty;

    [StringLength(255)]
    public string? Email { get; set; }

    [StringLength(50)]
    public string? PhoneNumber { get; set; }

    [StringLength(255)]
    public string? RegAddressCareOf { get; set; }

    [StringLength(100)]
    public string? RegAddressPoBox { get; set; }

    [StringLength(255)]
    public string? RegAddressAddressLine1 { get; set; }

    [StringLength(255)]
    public string? RegAddressAddressLine2 { get; set; }

    [StringLength(255)]
    public string? RegAddressPostTown { get; set; }

    [StringLength(255)]
    public string? RegAddressCounty { get; set; }

    [StringLength(255)]
    public string? RegAddressCountry { get; set; }

    [StringLength(20)]
    public string? RegAddressPostCode { get; set; }

    [StringLength(20)]
    public string? NormalizedPostcode { get; private set; }

    public double? Latitude { get; set; }

    public double? Longitude { get; set; }

    public Point? Location { get; set; }

    [StringLength(30)]
    public string? LocationLookupStatus { get; set; }

    [StringLength(255)]
    public string? LocationLookupMessage { get; set; }

    public DateTime? LocationLookupAtUtc { get; set; }

    [StringLength(255)]
    public string? CompanyCategory { get; set; }

    [StringLength(100)]
    public string? CompanyStatus { get; set; }

    [StringLength(100)]
    public string? CountryOfOrigin { get; set; }

    [StringLength(20)]
    public string? DissolutionDate { get; set; }

    [StringLength(20)]
    public string? IncorporationDate { get; set; }

    [StringLength(2)]
    public string? AccountsAccountRefDay { get; set; }

    [StringLength(2)]
    public string? AccountsAccountRefMonth { get; set; }

    [StringLength(20)]
    public string? AccountsNextDueDate { get; set; }

    [StringLength(20)]
    public string? AccountsLastMadeUpDate { get; set; }

    [StringLength(100)]
    public string? AccountsAccountCategory { get; set; }

    [StringLength(20)]
    public string? ReturnsNextDueDate { get; set; }

    [StringLength(20)]
    public string? ReturnsLastMadeUpDate { get; set; }

    [StringLength(20)]
    public string? MortgagesNumMortCharges { get; set; }

    [StringLength(20)]
    public string? MortgagesNumMortOutstanding { get; set; }

    [StringLength(20)]
    public string? MortgagesNumMortPartSatisfied { get; set; }

    [StringLength(20)]
    public string? MortgagesNumMortSatisfied { get; set; }

    [StringLength(255)]
    public string? SicCodeSicText1 { get; set; }

    [StringLength(255)]
    public string? SicCodeSicText2 { get; set; }

    [StringLength(255)]
    public string? SicCodeSicText3 { get; set; }

    [StringLength(255)]
    public string? SicCodeSicText4 { get; set; }

    [StringLength(20)]
    public string? LimitedPartnershipsNumGenPartners { get; set; }

    [StringLength(20)]
    public string? LimitedPartnershipsNumLimPartners { get; set; }

    [StringLength(500)]
    public string? Uri { get; set; }

    [StringLength(20)]
    public string? PreviousName1ConDate { get; set; }

    [StringLength(512)]
    public string? PreviousName1CompanyName { get; set; }

    [StringLength(20)]
    public string? PreviousName2ConDate { get; set; }

    [StringLength(512)]
    public string? PreviousName2CompanyName { get; set; }

    [StringLength(20)]
    public string? PreviousName3ConDate { get; set; }

    [StringLength(512)]
    public string? PreviousName3CompanyName { get; set; }

    [StringLength(20)]
    public string? PreviousName4ConDate { get; set; }

    [StringLength(512)]
    public string? PreviousName4CompanyName { get; set; }

    [StringLength(20)]
    public string? PreviousName5ConDate { get; set; }

    [StringLength(512)]
    public string? PreviousName5CompanyName { get; set; }

    [StringLength(20)]
    public string? PreviousName6ConDate { get; set; }

    [StringLength(512)]
    public string? PreviousName6CompanyName { get; set; }

    [StringLength(20)]
    public string? PreviousName7ConDate { get; set; }

    [StringLength(512)]
    public string? PreviousName7CompanyName { get; set; }

    [StringLength(20)]
    public string? PreviousName8ConDate { get; set; }

    [StringLength(512)]
    public string? PreviousName8CompanyName { get; set; }

    [StringLength(20)]
    public string? PreviousName9ConDate { get; set; }

    [StringLength(512)]
    public string? PreviousName9CompanyName { get; set; }

    [StringLength(20)]
    public string? PreviousName10ConDate { get; set; }

    [StringLength(512)]
    public string? PreviousName10CompanyName { get; set; }

    [StringLength(20)]
    public string? ConfStmtNextDueDate { get; set; }

    [StringLength(20)]
    public string? ConfStmtLastMadeUpDate { get; set; }

    public DateTime ImportedAtUtc { get; set; } = DateTime.UtcNow;

    [NotMapped]
    public double? DistanceKm { get; set; }
}
