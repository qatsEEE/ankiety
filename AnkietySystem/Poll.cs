namespace AnkietySystem;

public class Poll
{
    public int Id { get; set; }
    public string Question { get; set; }
    public List<PollOption> Options { get; set; } = new();
}