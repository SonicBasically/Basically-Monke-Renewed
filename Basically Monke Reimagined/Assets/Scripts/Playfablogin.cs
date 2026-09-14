using System;
using System.Collections;
using System.Collections.Generic;
using PlayFab;
using PlayFab.ClientModels;
using TMPro;
using UnityEngine;
using UnityEngine.SceneManagement;

/// <summary>
/// Handles PlayFab login, profile/inventory loading, and virtual currency tracking.
/// Fires <see cref="OnLoginComplete"/> as soon as an attempt succeeds or fails,
/// so callers (e.g. a retry script) don't need to poll.
/// </summary>
public class Playfablogin : MonoBehaviour
{
    [Header("COSMETICS")]
    public static Playfablogin instance;
    public string MyPlayFabID;
    public string CatalogName;

    // Existing scene objects to toggle based on owned items.
    public List<GameObject> specialItems;

    // Prefabs to spawn if the corresponding item is owned.
    public List<GameObject> specialItemPrefabs;

    // Objects to disable when a corresponding item is owned.
    public List<GameObject> disableItems;

    [Header("CURRENCY")]
    public TextMeshPro currencyText;
    [Tooltip("PlayFab virtual currency code, e.g. 'MN'.")]
    public string currencyCode = "MN";
    [SerializeField]
    public int coins;

    [Header("CURRENCY UPDATE SETTINGS")]
    [SerializeField] private float currencyUpdateInterval = 30f;
    [SerializeField] private bool enablePeriodicUpdates = true;
    private Coroutine currencyUpdateCoroutine;

    [Header("BANNED")]
    public string bannedSceneName;

    [Header("PLAYER DATA")]
    public TextMeshPro UserName;
    public string StartingUsername;
    public string playerName;
    [SerializeField]
    public bool UpdateName;

    private bool fullyLoaded = false;

    /// <summary>Fired once per login attempt with the final success/failure result.</summary>
    public event Action<bool> OnLoginComplete;

    private bool accountInfoDone;
    private bool inventoryDone;
    private bool loginInProgress;

    public bool IsFullyLoaded()
    {
        return fullyLoaded;
    }

    public void Awake()
    {
        instance = this;
    }

    void Start()
    {
        // login(); // Moved to retry logic
    }

    /// <summary>Starts (or restarts) a login attempt. Safe to call repeatedly; ignored while already in progress.</summary>
    public void login()
    {
        if (loginInProgress)
        {
            Debug.LogWarning("[PlayFab] Login already in progress, ignoring duplicate call.");
            return;
        }

        loginInProgress = true;
        fullyLoaded = false;
        accountInfoDone = false;
        inventoryDone = false;

        var request = new LoginWithCustomIDRequest
        {
            CustomId = SystemInfo.deviceUniqueIdentifier,
            CreateAccount = true,
            InfoRequestParameters = new GetPlayerCombinedInfoRequestParams
            {
                GetPlayerProfile = true
            }
        };
        PlayFabClientAPI.LoginWithCustomID(request, OnLoginSuccess, HandleError);
    }

    public void OnLoginSuccess(LoginResult result)
    {
        Debug.Log("[PlayFab] Login call succeeded, fetching account info + inventory...");

        GetAccountInfoRequest InfoRequest = new GetAccountInfoRequest();
        PlayFabClientAPI.GetAccountInfo(InfoRequest, AccountInfoSuccess, HandleError);

        // Single inventory fetch, reused for both cosmetics and currency
        // (the original code fetched inventory twice - once here, once in
        // AccountInfoSuccess - this version only does it once).
        PlayFabClientAPI.GetUserInventory(new GetUserInventoryRequest(), OnGetUserInventorySuccess, HandleError);

        if (enablePeriodicUpdates)
        {
            StartPeriodicCurrencyUpdates();
        }
    }

    public void AccountInfoSuccess(GetAccountInfoResult result)
    {
        MyPlayFabID = result.AccountInfo.PlayFabId;
        accountInfoDone = true;
        TryFinishLogin();
    }

    private void OnGetUserInventorySuccess(GetUserInventoryResult inventoryResult)
    {
        foreach (var item in inventoryResult.Inventory)
        {
            if (item.CatalogVersion == CatalogName)
            {
                for (int i = 0; i < specialItems.Count; i++)
                {
                    if (specialItems[i].name == item.ItemId)
                    {
                        specialItems[i].SetActive(true);
                    }
                }

                for (int i = 0; i < specialItemPrefabs.Count; i++)
                {
                    if (specialItemPrefabs[i].name == item.ItemId)
                    {
                        Instantiate(specialItemPrefabs[i]);
                    }
                }

                for (int i = 0; i < disableItems.Count; i++)
                {
                    if (disableItems[i].name == item.ItemId)
                    {
                        disableItems[i].SetActive(false);
                    }
                }
            }
        }

        UpdateCurrencyFromInventory(inventoryResult);
        inventoryDone = true;
        TryFinishLogin();
    }

    private void TryFinishLogin()
    {
        if (!accountInfoDone || !inventoryDone)
            return;

        loginInProgress = false;
        fullyLoaded = true;
        Debug.Log("[PlayFab] Fully loaded.");
        OnLoginComplete?.Invoke(true);
    }

    private void HandleError(PlayFabError error)
    {
        Debug.LogError($"[PlayFab] Error: {error.GenerateErrorReport()}");

        loginInProgress = false;
        fullyLoaded = false;

        if (error.Error == PlayFabErrorCode.AccountBanned)
        {
            if (!string.IsNullOrEmpty(bannedSceneName))
                SceneManager.LoadScene(bannedSceneName);

            // Don't signal for a retry on a ban - it isn't going to succeed.
            return;
        }

        // Any other error (network, server, bad request, etc.) fails the whole
        // attempt immediately so the retry script can react without waiting
        // out a full timeout.
        OnLoginComplete?.Invoke(false);
    }

    void Update()
    {
        // Your Update logic
    }

    #region Currency Management

    public void GetVirtualCurrencies()
    {
        PlayFabClientAPI.GetUserInventory(new GetUserInventoryRequest(), OnGetUserInventorySuccess, HandleError);
    }

    private void UpdateCurrencyFromInventory(GetUserInventoryResult result)
    {
        if (result.VirtualCurrency == null || !result.VirtualCurrency.TryGetValue(currencyCode, out int newCoins))
        {
            Debug.LogWarning($"[PlayFab] Currency code '{currencyCode}' not present in inventory response.");
            return;
        }

        // Only update if the value has changed
        if (newCoins != coins)
        {
            coins = newCoins;
            UpdateCurrencyDisplay();
            Debug.Log($"Currency updated: {coins}");
        }
    }

    private void UpdateCurrencyDisplay()
    {
        if (currencyText != null)
        {
            currencyText.text = "You Have: " + coins.ToString() + " Crystals";
        }
    }

    // Method to manually refresh currency (can be called from UI buttons, etc.)
    public void RefreshCurrency()
    {
        GetVirtualCurrencies();
    }

    // Periodic currency updates
    private void StartPeriodicCurrencyUpdates()
    {
        if (currencyUpdateCoroutine != null)
        {
            StopCoroutine(currencyUpdateCoroutine);
        }
        currencyUpdateCoroutine = StartCoroutine(PeriodicCurrencyUpdate());
    }

    private void StopPeriodicCurrencyUpdates()
    {
        if (currencyUpdateCoroutine != null)
        {
            StopCoroutine(currencyUpdateCoroutine);
            currencyUpdateCoroutine = null;
        }
    }

    private IEnumerator PeriodicCurrencyUpdate()
    {
        var wait = new WaitForSeconds(currencyUpdateInterval);
        while (enablePeriodicUpdates && fullyLoaded)
        {
            yield return wait;
            if (enablePeriodicUpdates && fullyLoaded)
                GetVirtualCurrencies();
        }
    }

    // Call this method after any transaction that might change currency
    public void OnCurrencyTransaction()
    {
        // Refresh currency immediately after a transaction
        GetVirtualCurrencies();
    }

    // Method to enable/disable periodic updates
    public void SetPeriodicUpdates(bool enabled)
    {
        enablePeriodicUpdates = enabled;
        if (enabled && fullyLoaded)
        {
            StartPeriodicCurrencyUpdates();
        }
        else
        {
            StopPeriodicCurrencyUpdates();
        }
    }

    #endregion

    // Clean up coroutines when the object is destroyed
    private void OnDestroy()
    {
        StopPeriodicCurrencyUpdates();
        if (instance == this)
        {
            instance = null;
        }
    }

    // Pause/resume periodic updates when the application loses/gains focus
    private void OnApplicationFocus(bool hasFocus)
    {
        if (hasFocus && enablePeriodicUpdates && fullyLoaded)
        {
            StartPeriodicCurrencyUpdates();
        }
        else
        {
            StopPeriodicCurrencyUpdates();
        }
    }
}