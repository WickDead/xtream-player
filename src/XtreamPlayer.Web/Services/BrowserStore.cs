using Microsoft.JSInterop;
using XtreamPlayer.Application;
namespace XtreamPlayer.Web.Services;
public sealed class BrowserStore(IJSRuntime js) : IDeviceStorage
{
    public ValueTask<T?> GetAsync<T>(string key) => js.InvokeAsync<T?>("xtream.get", key);
    public ValueTask SetAsync<T>(string key, T value) => js.InvokeVoidAsync("xtream.set", key, value);
    public ValueTask RemoveAsync(string key) => js.InvokeVoidAsync("xtream.remove", key);
}
