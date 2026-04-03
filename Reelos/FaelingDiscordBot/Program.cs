// Exception handling updated in Program.cs

public class Bot
{
    public async Task OnInteractionCreatedAsync()
    {
        try
        {
            // Your existing code for handling interactions
        }
        catch (Exception ex)
        {
            // Log the exception and handle it appropriately
            Console.WriteLine("Error handling interaction: " + ex.Message);
        }
    }

    public async Task OnReadyAsync()
    {
        try
        {
            // Your existing code for when the bot is ready
        }
        catch (Exception ex)
        {
            // Log the exception and handle it appropriately
            Console.WriteLine("Error in OnReadyAsync: " + ex.Message);
        }
    }

    public async Task OnJoinedGuildAsync()
    {
        try
        {
            // Your existing code for when the bot joins a guild
        }
        catch (Exception ex)
        {
            // Log the exception and handle it appropriately
            Console.WriteLine("Error in OnJoinedGuildAsync: " + ex.Message);
        }
    }
}