using System.Net;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DigitalButler.Skills;

public sealed class UnsplashOptions
{
    public string? AccessKey { get; set; }
}

public sealed class PexelsOptions
{
    public string? ApiKey { get; set; }
}

public enum ImageSource
{
    Unsplash,
    Pexels
}

public readonly record struct DrawingReferenceResult(
    string ImageUrl,
    string PhotoPageUrl,
    string PhotographerName,
    string PhotographerProfileUrl,
    ImageSource Source);

public interface IRandomDrawingTopicService
{
    string GetRandomTopic();
}

public sealed class RandomDrawingTopicService : IRandomDrawingTopicService
{
    private static readonly string[] Topics =
    [
        // People & anatomy - faces
        "hands", "portrait", "eyes", "lips", "nose", "ears", "profile portrait", "three-quarter view face",
        "elderly face", "child portrait", "expressive face", "laughing face", "crying face", "angry expression",
        "surprised expression", "contemplative face", "sleeping face", "face with glasses", "bearded man",
        "woman with long hair", "bald head", "face with freckles", "face in shadow", "backlit portrait",

        // People & anatomy - body parts
        "feet", "fingers", "fist", "open palm", "pointing hand", "clasped hands", "hand holding object",
        "arm muscles", "shoulder", "neck", "back muscles", "torso", "leg anatomy", "knee", "elbow",
        "wrist", "ankle", "collarbone", "ribcage", "spine curve",

        // People & anatomy - full figure
        "figure drawing", "gesture drawing", "seated figure", "standing pose", "walking pose", "running pose",
        "dancing figure", "reclining figure", "crouching pose", "jumping pose", "figure from behind",
        "figure in motion", "contraposto pose", "twisted torso", "foreshortened figure", "figure with drapery",
        "silhouette figure", "figure in doorway", "person reading", "person at desk",

        // People - occupations & activities
        "musician playing", "chef cooking", "artist painting", "dancer mid-movement", "athlete in action",
        "person meditating", "person stretching", "craftsman working", "gardener", "fisherman",
        "street performer", "person on phone", "person typing", "person carrying bags",

        // Animals - pets & domestic
        "cat sleeping", "cat stretching", "dog portrait", "puppy playing", "cat paw", "dog paw",
        "rabbit", "hamster", "guinea pig", "goldfish", "parrot", "canary", "turtle", "lizard",

        // Animals - wildlife mammals
        "wolf", "fox", "deer", "elk", "moose", "bear", "lion", "tiger", "leopard", "cheetah",
        "elephant", "giraffe", "zebra", "rhinoceros", "hippopotamus", "gorilla", "orangutan",
        "monkey", "squirrel", "raccoon", "badger", "otter", "beaver", "hedgehog", "bat",
        "kangaroo", "koala", "panda", "polar bear", "seal", "walrus", "dolphin", "whale",

        // Animals - birds
        "eagle", "hawk", "owl", "crow", "raven", "sparrow", "robin", "blue jay", "cardinal",
        "hummingbird", "peacock", "flamingo", "swan", "duck", "goose", "pelican", "heron",
        "crane", "penguin", "puffin", "toucan", "kingfisher", "woodpecker", "pheasant",

        // Animals - reptiles & amphibians
        "snake", "cobra", "python", "crocodile", "alligator", "iguana", "chameleon", "gecko",
        "frog", "toad", "salamander", "sea turtle", "tortoise", "dragon lizard",

        // Animals - insects & small creatures
        "butterfly", "moth", "dragonfly", "bee", "bumblebee", "ladybug", "beetle", "ant",
        "grasshopper", "praying mantis", "spider", "scorpion", "snail", "caterpillar",

        // Animals - sea life
        "fish", "tropical fish", "koi", "shark", "octopus", "squid", "jellyfish", "seahorse",
        "starfish", "crab", "lobster", "shrimp", "coral", "sea anemone", "manta ray",

        // Animals - mythical (for creative practice)
        "dragon", "phoenix", "unicorn", "griffin", "pegasus", "mermaid", "centaur",

        // Objects - furniture
        "chair", "armchair", "rocking chair", "stool", "bench", "sofa", "bed", "desk",
        "table", "coffee table", "bookshelf", "wardrobe", "dresser", "mirror", "lamp",
        "chandelier", "grandfather clock",

        // Objects - household items
        "cup", "mug", "teapot", "kettle", "plate", "bowl", "spoon", "fork", "knife",
        "wine glass", "bottle", "pitcher", "vase", "candlestick", "picture frame",
        "umbrella", "basket", "bucket", "broom", "iron", "sewing machine",

        // Objects - personal items
        "shoes", "boots", "sneakers", "high heels", "sandals", "hat", "cap", "glasses",
        "sunglasses", "watch", "ring", "necklace", "bracelet", "earrings", "handbag",
        "backpack", "wallet", "keys", "phone", "headphones",

        // Objects - tools & equipment
        "hammer", "screwdriver", "wrench", "pliers", "saw", "drill", "paintbrush",
        "scissors", "needle and thread", "measuring tape", "compass", "magnifying glass",
        "binoculars", "telescope", "microscope", "camera", "tripod",

        // Objects - musical instruments
        "guitar", "acoustic guitar", "electric guitar", "violin", "cello", "piano keys",
        "trumpet", "saxophone", "flute", "clarinet", "drums", "tambourine", "harmonica",
        "accordion", "harp", "banjo", "ukulele", "microphone",

        // Objects - technology & vehicles
        "bicycle", "motorcycle", "car", "vintage car", "truck", "bus", "train",
        "airplane", "helicopter", "boat", "sailboat", "ship", "hot air balloon",
        "typewriter", "radio", "record player", "television", "computer",

        // Objects - books & stationery
        "book", "open book", "stack of books", "notebook", "pen", "pencil", "ink bottle",
        "quill", "scroll", "envelope", "letter", "newspaper", "magazine",

        // Objects - sports & games
        "soccer ball", "basketball", "tennis racket", "baseball glove", "golf club",
        "skateboard", "surfboard", "ski", "ice skate", "chess pieces", "playing cards", "dice",

        // Nature - trees
        "oak tree", "pine tree", "willow tree", "birch tree", "maple tree", "palm tree",
        "cherry blossom tree", "bare winter tree", "tree trunk", "tree roots", "tree bark",
        "branch with leaves", "fallen tree", "bonsai tree", "old gnarled tree",

        // Nature - plants & flowers
        "rose", "tulip", "sunflower", "daisy", "lily", "orchid", "lotus", "poppy",
        "lavender", "dandelion", "wildflowers", "fern", "ivy", "moss", "mushroom",
        "cactus", "succulent", "potted plant", "flower bouquet", "dried flowers",
        "wheat", "bamboo", "grass blades", "clover",

        // Nature - landscapes
        "mountains", "mountain peak", "rolling hills", "valley", "canyon", "cliff",
        "desert dunes", "beach", "coastline", "island", "lake", "river", "stream",
        "waterfall", "pond", "swamp", "meadow", "prairie", "tundra", "glacier",

        // Nature - sky & weather
        "clouds", "storm clouds", "cumulus clouds", "sunset", "sunrise", "night sky",
        "full moon", "crescent moon", "stars", "milky way", "northern lights",
        "rainbow", "lightning", "rain", "snow falling", "fog", "mist",

        // Nature - water
        "ocean waves", "calm water reflection", "ripples", "waterfall close-up",
        "river rapids", "frozen lake", "icicles", "water droplets", "splash",

        // Nature - rocks & geology
        "rocks", "boulders", "pebbles", "cliff face", "cave entrance", "stalactites",
        "geode", "crystal", "volcanic rock", "sedimentary layers", "fossils",

        // Architecture - buildings
        "house", "cottage", "cabin", "farmhouse", "mansion", "castle", "palace",
        "skyscraper", "apartment building", "office building", "factory", "warehouse",
        "barn", "windmill", "lighthouse", "church", "cathedral", "mosque", "temple",
        "pagoda", "pyramid", "colosseum", "ruins",

        // Architecture - elements
        "window", "arched window", "stained glass window", "door", "ornate doorway",
        "archway", "column", "pillar", "balcony", "porch", "stairs", "spiral staircase",
        "roof", "chimney", "tower", "dome", "spire", "gargoyle", "fountain",

        // Architecture - structures
        "bridge", "suspension bridge", "stone bridge", "covered bridge", "aqueduct",
        "pier", "dock", "boardwalk", "fence", "gate", "wall", "stone wall",

        // Architecture - interiors & urban
        "interior room", "living room corner", "kitchen", "bathroom", "bedroom",
        "library interior", "cafe interior", "restaurant", "bar", "shop window",
        "street scene", "alley", "courtyard", "garden path", "park bench",
        "cityscape", "rooftops", "fire escape", "subway station", "train station",

        // Food - fruits
        "apple", "pear", "orange", "lemon", "lime", "banana", "grapes", "strawberry",
        "raspberry", "blueberry", "cherry", "peach", "plum", "mango", "pineapple",
        "watermelon", "melon", "kiwi", "pomegranate", "fig", "coconut",

        // Food - vegetables
        "tomato", "pepper", "onion", "garlic", "carrot", "potato", "eggplant",
        "zucchini", "cucumber", "lettuce", "cabbage", "broccoli", "cauliflower",
        "corn", "peas", "beans", "asparagus", "artichoke", "pumpkin", "squash",

        // Food - prepared foods
        "bread loaf", "baguette", "croissant", "bagel", "pizza", "burger", "sandwich",
        "pasta", "noodles", "sushi", "soup bowl", "salad", "steak", "roast chicken",
        "fried egg", "pancakes", "waffles", "toast",

        // Food - desserts & sweets
        "cake", "cupcake", "pie slice", "donut", "cookie", "chocolate", "ice cream",
        "macarons", "candy", "lollipop", "gingerbread", "fruit tart", "cheesecake",

        // Food - drinks
        "coffee cup", "tea cup", "glass of water", "juice glass", "wine glass with wine",
        "beer mug", "cocktail", "smoothie", "milk bottle", "coffee beans",

        // Food - ingredients & kitchen
        "eggs", "cheese wheel", "cheese wedge", "butter", "honey jar", "olive oil bottle",
        "salt and pepper shakers", "herbs", "spices", "flour bag", "sugar bowl",
        "mixing bowl", "whisk", "rolling pin", "cutting board", "chef knife",

        // Fabric & texture
        "drapery", "fabric folds", "silk texture", "velvet texture", "lace pattern",
        "knitted fabric", "woven basket texture", "burlap", "canvas texture",
        "leather texture", "suede texture", "fur texture", "wool texture",
        "feathers", "scales pattern", "wood grain", "tree bark texture",
        "stone texture", "brick wall", "concrete texture", "rust texture",
        "metal surface", "brushed metal", "corrugated metal", "chain links",
        "glass surface", "cracked glass", "water on glass", "frosted glass",
        "paper texture", "crumpled paper", "torn paper", "old parchment",
        "rope", "twine", "chain", "wire", "net", "mesh",

        // Still life compositions
        "still life with fruit", "still life with flowers", "breakfast still life",
        "kitchen still life", "desk still life", "artist studio still life",
        "vanitas still life", "vintage objects arrangement", "bottles and jars",
        "candles and books", "musical instruments arrangement", "sports equipment",
        "gardening tools", "sewing supplies", "art supplies", "beach objects",
        "autumn leaves arrangement", "holiday decorations", "antique collection",
        "seashells and driftwood", "crystals and minerals", "old photographs and letters",

        // Lighting studies
        "candlelit scene", "window light portrait", "backlit silhouette",
        "harsh sunlight shadows", "soft diffused light", "dramatic chiaroscuro",
        "golden hour lighting", "blue hour scene", "neon lights", "lamp light",
        "firelight", "reflected light", "dappled light through leaves",

        // Perspective & composition challenges
        "looking up at buildings", "looking down from height", "worm's eye view",
        "bird's eye view", "through a window", "reflection in mirror",
        "reflection in water", "view through doorway", "framed by arch",
        "extreme close-up", "wide panoramic view", "fish-eye perspective",

        // Seasonal & weather themes
        "spring blossoms", "summer beach scene", "autumn leaves", "winter snow scene",
        "rainy day", "foggy morning", "sunny afternoon", "stormy sky",
        "frost patterns", "melting snow", "spring puddles", "autumn harvest",

        // Time of day themes
        "early morning light", "midday shadows", "afternoon tea", "golden sunset",
        "twilight silhouettes", "moonlit night", "starry night", "city at night",
        "dawn breaking", "dusk settling"
    ];

    public string GetRandomTopic()
    {
        return Topics[Random.Shared.Next(Topics.Length)];
    }
}

public interface IDrawingReferenceService
{
    Task<DrawingReferenceResult?> GetReferenceAsync(string subject, CancellationToken ct = default);
}

public sealed class UnsplashDrawingReferenceService : IDrawingReferenceService
{
    private readonly HttpClient _http;
    private readonly UnsplashOptions _options;
    private readonly ILogger<UnsplashDrawingReferenceService> _logger;

    public UnsplashDrawingReferenceService(HttpClient http, IOptions<UnsplashOptions> options, ILogger<UnsplashDrawingReferenceService> logger)
    {
        _http = http;
        _options = options.Value;
        _logger = logger;
    }

    public async Task<DrawingReferenceResult?> GetReferenceAsync(string subject, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(subject))
        {
            return null;
        }

        var accessKey = _options.AccessKey;
        if (string.IsNullOrWhiteSpace(accessKey))
        {
            throw new InvalidOperationException("Unsplash access key is not configured (Unsplash:AccessKey / UNSPLASH_ACCESS_KEY).");
        }

        var query = subject.Trim();
        var url = "https://api.unsplash.com/search/photos" +
                  "?query=" + WebUtility.UrlEncode(query) +
                  "&per_page=30" +
                  "&content_filter=high" +
                  "&orientation=portrait";

        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.TryAddWithoutValidation("Authorization", $"Client-ID {accessKey}");
        req.Headers.TryAddWithoutValidation("Accept-Version", "v1");

        using var resp = await _http.SendAsync(req, ct);
        var raw = await resp.Content.ReadAsStringAsync(ct);
        if (!resp.IsSuccessStatusCode)
        {
            _logger.LogWarning("Unsplash request failed: {Status} {Reason}. Body: {Body}", (int)resp.StatusCode, resp.ReasonPhrase, raw.Length <= 2000 ? raw : raw[..2000] + "…");
            throw new HttpRequestException($"Unsplash request failed with {(int)resp.StatusCode} {resp.ReasonPhrase}.");
        }

        using var doc = JsonDocument.Parse(raw);
        var root = doc.RootElement;

        if (!root.TryGetProperty("results", out var results) || results.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        var resultsArray = results.EnumerateArray().ToArray();
        if (resultsArray.Length == 0)
        {
            return null;
        }

        // Pick a random result to provide variety when requesting "different image"
        var selected = resultsArray[Random.Shared.Next(resultsArray.Length)];
        if (selected.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        var imageUrl = selected.GetProperty("urls").GetProperty("regular").GetString();
        var photoPageUrl = selected.GetProperty("links").GetProperty("html").GetString();
        var photographerName = selected.GetProperty("user").GetProperty("name").GetString();
        var photographerProfileUrl = selected.GetProperty("user").GetProperty("links").GetProperty("html").GetString();

        if (string.IsNullOrWhiteSpace(imageUrl) || string.IsNullOrWhiteSpace(photoPageUrl) || string.IsNullOrWhiteSpace(photographerName) || string.IsNullOrWhiteSpace(photographerProfileUrl))
        {
            return null;
        }

        return new DrawingReferenceResult(imageUrl, photoPageUrl, photographerName, photographerProfileUrl, ImageSource.Unsplash);
    }
}

public sealed class PexelsDrawingReferenceService : IDrawingReferenceService
{
    private readonly HttpClient _http;
    private readonly PexelsOptions _options;
    private readonly ILogger<PexelsDrawingReferenceService> _logger;

    public PexelsDrawingReferenceService(HttpClient http, IOptions<PexelsOptions> options, ILogger<PexelsDrawingReferenceService> logger)
    {
        _http = http;
        _options = options.Value;
        _logger = logger;
    }

    public async Task<DrawingReferenceResult?> GetReferenceAsync(string subject, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(subject))
        {
            return null;
        }

        var apiKey = _options.ApiKey;
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            throw new InvalidOperationException("Pexels API key is not configured (Pexels:ApiKey / PEXELS_API_KEY).");
        }

        var query = subject.Trim();
        var url = "https://api.pexels.com/v1/search" +
                  "?query=" + WebUtility.UrlEncode(query) +
                  "&per_page=30" +
                  "&orientation=portrait";

        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.TryAddWithoutValidation("Authorization", apiKey);

        using var resp = await _http.SendAsync(req, ct);
        var raw = await resp.Content.ReadAsStringAsync(ct);
        if (!resp.IsSuccessStatusCode)
        {
            _logger.LogWarning("Pexels request failed: {Status} {Reason}. Body: {Body}", (int)resp.StatusCode, resp.ReasonPhrase, raw.Length <= 2000 ? raw : raw[..2000] + "…");
            throw new HttpRequestException($"Pexels request failed with {(int)resp.StatusCode} {resp.ReasonPhrase}.");
        }

        using var doc = JsonDocument.Parse(raw);
        var root = doc.RootElement;

        if (!root.TryGetProperty("photos", out var photos) || photos.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        var photosArray = photos.EnumerateArray().ToArray();
        if (photosArray.Length == 0)
        {
            return null;
        }

        var selected = photosArray[Random.Shared.Next(photosArray.Length)];
        if (selected.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        var imageUrl = selected.GetProperty("src").GetProperty("large").GetString();
        var photoPageUrl = selected.GetProperty("url").GetString();
        var photographerName = selected.GetProperty("photographer").GetString();
        var photographerProfileUrl = selected.GetProperty("photographer_url").GetString();

        if (string.IsNullOrWhiteSpace(imageUrl) || string.IsNullOrWhiteSpace(photoPageUrl) || string.IsNullOrWhiteSpace(photographerName) || string.IsNullOrWhiteSpace(photographerProfileUrl))
        {
            return null;
        }

        return new DrawingReferenceResult(imageUrl, photoPageUrl, photographerName, photographerProfileUrl, ImageSource.Pexels);
    }
}

public interface ICompositeDrawingReferenceService
{
    Task<DrawingReferenceResult?> GetReferenceAsync(string subject, CancellationToken ct = default);
    Task<DrawingReferenceResult?> GetReferenceFromSourceAsync(string subject, ImageSource source, CancellationToken ct = default);
}

public sealed class CompositeDrawingReferenceService : ICompositeDrawingReferenceService
{
    private readonly UnsplashDrawingReferenceService _unsplash;
    private readonly PexelsDrawingReferenceService _pexels;
    private readonly ILogger<CompositeDrawingReferenceService> _logger;

    public CompositeDrawingReferenceService(
        UnsplashDrawingReferenceService unsplash,
        PexelsDrawingReferenceService pexels,
        ILogger<CompositeDrawingReferenceService> logger)
    {
        _unsplash = unsplash;
        _pexels = pexels;
        _logger = logger;
    }

    public async Task<DrawingReferenceResult?> GetReferenceAsync(string subject, CancellationToken ct = default)
    {
        var source = Random.Shared.Next(2) == 0 ? ImageSource.Unsplash : ImageSource.Pexels;
        return await GetReferenceFromSourceAsync(subject, source, ct);
    }

    public async Task<DrawingReferenceResult?> GetReferenceFromSourceAsync(string subject, ImageSource source, CancellationToken ct = default)
    {
        try
        {
            return source switch
            {
                ImageSource.Unsplash => await _unsplash.GetReferenceAsync(subject, ct),
                ImageSource.Pexels => await _pexels.GetReferenceAsync(subject, ct),
                _ => await _unsplash.GetReferenceAsync(subject, ct)
            };
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to get reference from {Source}, trying fallback", source);
            var fallbackSource = source == ImageSource.Unsplash ? ImageSource.Pexels : ImageSource.Unsplash;
            return fallbackSource switch
            {
                ImageSource.Unsplash => await _unsplash.GetReferenceAsync(subject, ct),
                ImageSource.Pexels => await _pexels.GetReferenceAsync(subject, ct),
                _ => null
            };
        }
    }
}
