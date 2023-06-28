using System;
using System.Net;
using System.Net.Http;
using System.Reactive.Subjects;
using System.Text;
using System.Threading.Tasks;
using Newtonsoft.Json;

public class RepositoryInfo
{
    public string Author { get; set; }
    public int CommitCount { get; set; }
}

public class RepositoryStream : IObservable<RepositoryInfo>, IDisposable
{
    private readonly HttpListener listener;
    private readonly Subject<RepositoryInfo> subject;
    private readonly string accessToken;

    public RepositoryStream(string accessToken)
    {
        listener = new HttpListener();
        listener.Prefixes.Add("http://localhost:5050/");
        subject = new Subject<RepositoryInfo>();
        this.accessToken = accessToken;
    }

    public IDisposable Subscribe(IObserver<RepositoryInfo> observer)
    {
        return subject.Subscribe(observer);
    }

    public void Start()
    {
        listener.Start();
        Console.WriteLine("Web server je pokrenut. Očekivanje zahteva...");

        while (listener.IsListening)
        {
            var context = listener.GetContext();
            _ = ProcessRequestAsync(context);
        }
    }

    public void Dispose()
    {
        listener.Stop();
        listener.Close();
        subject.OnCompleted();
        subject.Dispose();
    }

    private async Task ProcessRequestAsync(HttpListenerContext context)
    {
        Console.WriteLine("Zahtev primljen.");
        var request = context.Request;
        var response = context.Response;

        if (request.HttpMethod == "GET")
        {
            var language = request.QueryString["language"];

            if (!string.IsNullOrEmpty(language))
            {
                Console.WriteLine("Pretraga repozitorijuma za programski jezik: " + language + "\n");
                try
                {
                    HttpClient client = new HttpClient();
                    var url = $"https://api.github.com/search/repositories?q=language:{language}&sort=stars&order=desc";

                    client.DefaultRequestHeaders.Add("User-Agent", "GitHubRepoAnalyzer");
                    client.DefaultRequestHeaders.Add("Authorization", $"Bearer {accessToken}");

                    var responseMessage = await client.GetAsync(url);
                    responseMessage.EnsureSuccessStatusCode();
                    var content = await responseMessage.Content.ReadAsStringAsync();
                    var repositories = JsonConvert.DeserializeObject<dynamic>(content).items;

                    foreach (var repository in repositories)
                    {
                        var author = repository.owner.login?.ToString();
                        if (string.IsNullOrEmpty(author))
                        {
                            Console.WriteLine("Nevalidan autor repozitorijuma. Preskačem obradu.");
                            continue;
                        }

                        var commitUrl = $"https://api.github.com/repos/{repository.full_name}/commits?author={author}";
                        var commitCount = await GetActualCommitCountAsync(repository.full_name.ToString(), author);

                        var repositoryInfo = new RepositoryInfo
                        {
                            Author = author,
                            CommitCount = commitCount,
                        };

                        subject.OnNext(repositoryInfo);
                    }

                    var responseContent = JsonConvert.SerializeObject(repositories);
                    await WriteResponseAsync(response, HttpStatusCode.OK, responseContent);
                    subject.OnCompleted();
                }
                catch (Exception e)
                {
                    var errorMessage = $"Došlo je do greške: {e.Message}";
                    await WriteResponseAsync(response, HttpStatusCode.InternalServerError, errorMessage);
                    subject.OnError(e);
                }
            }
            else
            {
                var errorMessage = "Nedostaje parametar jezik (language) u upitu.";
                await WriteResponseAsync(response, HttpStatusCode.BadRequest, errorMessage);
            }
        }
        else
        {
            var errorMessage = "Metoda zahteva nije podržana.";
            await WriteResponseAsync(response, HttpStatusCode.MethodNotAllowed, errorMessage);
            subject.OnError(new NotSupportedException(errorMessage));
        }

        response.Close();
    }

    private async Task<int> GetActualCommitCountAsync(string repositoryFullName, string author)
    {
        var url = $"https://api.github.com/repos/{repositoryFullName}/commits?author={author}";

        using (HttpClient client = new HttpClient())
        {
            client.DefaultRequestHeaders.Add("User-Agent", "GitHubRepoAnalyzer");
            client.DefaultRequestHeaders.Add("Authorization", $"Bearer {accessToken}");

            try
            {
                var response = await client.GetAsync(url);
                response.EnsureSuccessStatusCode();

                var content = await response.Content.ReadAsStringAsync();
                var commits = JsonConvert.DeserializeObject<dynamic>(content);

                return commits.Count;
            }
            catch (Exception e)
            {
                Console.WriteLine($"Greška prilikom dobavljanja broja commitova: {e.Message}");
                return 0;
            }
        }
    }

    private async Task WriteResponseAsync(HttpListenerResponse response, HttpStatusCode statusCode, string content)
    {
        response.StatusCode = (int)statusCode;
        response.ContentType = "application/json";
        byte[] buffer = Encoding.UTF8.GetBytes(content);
        response.ContentLength64 = buffer.Length;
        await response.OutputStream.WriteAsync(buffer, 0, buffer.Length);
        response.OutputStream.Close();
    }
}

public class Program
{
    public static void Main()
    {
        string accessToken = "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";

        Console.BackgroundColor = ConsoleColor.White;
        Console.ForegroundColor = ConsoleColor.Black;
        Console.Clear();

        using (var repositoryStream = new RepositoryStream(accessToken))
        {
            var observer = new ConsoleObserver("Observer");
            using (repositoryStream.Subscribe(observer))
            {
                repositoryStream.Start();
            }
        }
    }

    private class ConsoleObserver : IObserver<RepositoryInfo>
    {
        private readonly string _name;

        public ConsoleObserver(string name)
        {
            _name = name;
        }

        public void OnNext(RepositoryInfo value)
        {
            Console.WriteLine($"{_name}: Autor: {value.Author}\nBroj commitova: {value.CommitCount}\n");
        }

        public void OnError(Exception error)
        {
            Console.WriteLine($"{_name}: Došlo je do greške: {error.Message}");
        }

        public void OnCompleted()
        {
            Console.WriteLine($"{_name}: Završeno praćenje repozitorijuma.");
        }
    }
}

/*private async Task<int> GetCommitCountAsync(HttpClient client, string commitUrl, HttpListenerContext context, string author)
{
    var commitCount = 0;
    var page = 1;
    var perPage = 100; 

    try
    {
        while (true)
        {
            var paginatedUrl = $"{commitUrl}?author={author}&page={page}&per_page={perPage}";

            var responseMessage = await client.GetAsync(paginatedUrl);
            responseMessage.EnsureSuccessStatusCode();

            var commitContent = await responseMessage.Content.ReadAsStringAsync();
            var commits = JsonConvert.DeserializeObject<dynamic>(commitContent);

            if (commits.Count == 0)
            {
                break; 
            }

            commitCount += commits.Count;

            page++; // Prelazimo na sledecu stranicu
        }
    }
    catch (Exception e)
    {
        var errorMessage = $"Došlo je do greške pri dobijanju broja komitovanja za autora {author}: {e.Message}";
        await WriteResponseAsync(context.Response, HttpStatusCode.InternalServerError, errorMessage);
        subject.OnError(e);
    }

    return commitCount;
}
*/