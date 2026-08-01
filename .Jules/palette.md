## 2024-05-18 - Added required form indicators
**Learning:** Adding explicit required indicators to form fields helps improve usability by reducing the chance of user confusion regarding which fields are mandatory. Relying solely on the native `required` attribute may not be sufficient for all users, particularly those who benefit from visual cues.
**Action:** Always include a visual indicator (like an asterisk) and the `aria-required="true"` attribute on required form fields to explicitly signal their requirement status both visually and programmatically.
