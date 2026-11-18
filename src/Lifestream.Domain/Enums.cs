namespace Lifestream.Domain;

public enum Gender { Male, Female, Other }

/// <summary>Authorization role. The demo account is a Staff row with IsDemo set, not a role.</summary>
public enum Role { Admin, Staff }

/// <summary>Staff designation (the old app's values).</summary>
public enum Designation { Admin, Doctor, Worker, Assistant }

public enum Shift { Morning, Evening, FullTime }

public enum DonorFrequency { Regular, Occasional }

public enum AppointmentStatus { Pending, Completed, Cancelled, NoShow }

public enum RequestStatus { Pending, Fulfilled, Cancelled }

public enum ReceiptKind { Donation, Issue }

public enum BagStatus { Available, Issued, Discarded }
