namespace FunctionTesting;

public interface IBlobProcessor
{
    void Process(string name, string content);
}

public class BlobProcessor : IBlobProcessor
{
    public void Process(string name, string content)
    {
       
    }
}