## 2025-05-18 - Type missing on some interactive buttons
**Learning:** Found several Blazor Razor components that use <button> without an explicit type attribute, running the risk of implicit form submission when these buttons appear inside forms, even if they aren't right now, leading to unpredictable UI states.
**Action:** Adding explicit type="button" to <button> elements in Home.razor, Grimoire.razor, Players.razor, and CampaignList.razor where appropriate.
