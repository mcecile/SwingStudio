namespace SwingStudio.Session;

public sealed class SwingStroke
{
    public string Camera { get; set; } = "A";

    public string Tool { get; set; } = "line";

    public string Color { get; set; } = "#FFE08A";

    public double SessionMs { get; set; }

    public List<double> Points { get; set; } = [];
}
