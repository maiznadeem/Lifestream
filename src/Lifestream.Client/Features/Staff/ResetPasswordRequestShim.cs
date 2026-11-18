namespace Lifestream.Contracts.Staff;

// TEMPORARY SHIM (b-06-features2). The approved contract change adds `ResetPasswordRequest(string TemporaryPassword)` to Contracts/Staff and
// removes ResetPasswordResponse; that edit lands through the API-fix builder. Delete this file when that change is merged, otherwise the
// two declarations collide (CS0436).
public sealed record ResetPasswordRequest(string TemporaryPassword);
