using MyServe.Backend.Common.Abstract;

namespace MyServe.Backend.Common.Impl;

public class CacheControl : ICacheControl
{
    private readonly HashSet<string> _expiryKeys = [];
    private string _cacheKey = string.Empty;
    
    public IReadOnlySet<string> ExpiryKeys => _expiryKeys;
    
    public void AddKeyToExpire(params string[] keys)
    {
        var finalKey = string.Join("-", keys);
        if(!finalKey.EndsWith("-*"))
        {
            finalKey += "-*";
        }

        _expiryKeys.Add(finalKey);
    }

    public void AddExactKeyToExpire(params string[] keys)
    {
        var finalKey = string.Join("-", keys);
        _expiryKeys.Add(finalKey);
    }

    public string EndpointCacheKey => _cacheKey;

    public void FrameEndpointCacheKey(string module, params string[] keys)
    {
        _cacheKey = module + "-" + string.Join("-", keys);
    }

    public bool IsEndpointCached => !string.IsNullOrEmpty(_cacheKey);
}