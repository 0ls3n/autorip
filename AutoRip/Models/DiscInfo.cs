namespace AutoRip.Models;

public class DiscInfo
{
    public string DevicePath { get; set; } = string.Empty;
    public string DriveModel { get; set; } = string.Empty;
    public bool HasMedia { get; set; }
    public string Label { get; set; } = string.Empty;
    public MovieInfo? MovieInfo { get; set; }
    public bool IsIdentified => MovieInfo != null;
    public bool IsAmbiguous { get; set; }
    public bool IsEjecting { get; set; }

    public string DisplayTitle => MovieInfo?.Title ?? Label;
    public string DisplayName => string.IsNullOrEmpty(DriveModel) ? DevicePath : DriveModel;
}
