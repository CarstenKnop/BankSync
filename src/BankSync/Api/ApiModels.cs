using System.Text.Json.Serialization;

namespace BankSync.Api;

// DTOs for the Enable Banking endpoints the library uses. Property names follow the API reference.

internal sealed record Amount(
    [property: JsonPropertyName("currency")] string? Currency,
    [property: JsonPropertyName("amount")] string? Value);

internal sealed record AspspRef(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("country")] string Country);

internal sealed record Aspsp(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("country")] string Country,
    [property: JsonPropertyName("psu_types")] string[]? PsuTypes,
    [property: JsonPropertyName("maximum_consent_validity")] long MaximumConsentValiditySeconds,
    [property: JsonPropertyName("beta")] bool Beta,
    [property: JsonPropertyName("bic")] string? Bic,
    [property: JsonPropertyName("required_psu_headers")] string[]? RequiredPsuHeaders);

internal sealed record AspspsEnvelope([property: JsonPropertyName("aspsps")] Aspsp[] Aspsps);

internal sealed record Access(
    [property: JsonPropertyName("valid_until")] DateTimeOffset ValidUntil,
    [property: JsonPropertyName("balances")] bool? Balances = true,
    [property: JsonPropertyName("transactions")] bool? Transactions = true);

internal sealed record StartAuthorizationRequest(
    [property: JsonPropertyName("access")] Access Access,
    [property: JsonPropertyName("aspsp")] AspspRef Aspsp,
    [property: JsonPropertyName("state")] string State,
    [property: JsonPropertyName("redirect_url")] string RedirectUrl,
    [property: JsonPropertyName("psu_type")] string PsuType,
    [property: JsonPropertyName("language")] string? Language);

internal sealed record StartAuthorizationResponse(
    [property: JsonPropertyName("url")] string Url,
    [property: JsonPropertyName("authorization_id")] string AuthorizationId);

internal sealed record OtherIdentification(
    [property: JsonPropertyName("identification")] string? Identification,
    [property: JsonPropertyName("scheme_name")] string? SchemeName);

internal sealed record AccountIdentification(
    [property: JsonPropertyName("iban")] string? Iban,
    [property: JsonPropertyName("other")] OtherIdentification? Other);

internal sealed record AccountResource(
    [property: JsonPropertyName("uid")] string Uid,
    [property: JsonPropertyName("account_id")] AccountIdentification? AccountId,
    [property: JsonPropertyName("all_account_ids")] OtherIdentification[]? AllAccountIds,
    [property: JsonPropertyName("name")] string? Name,
    [property: JsonPropertyName("product")] string? Product,
    [property: JsonPropertyName("currency")] string? Currency,
    [property: JsonPropertyName("cash_account_type")] string? CashAccountType,
    [property: JsonPropertyName("usage")] string? Usage,
    [property: JsonPropertyName("identification_hash")] string? IdentificationHash);

internal sealed record AuthorizeSessionRequest([property: JsonPropertyName("code")] string Code);

internal sealed record AuthorizeSessionResponse(
    [property: JsonPropertyName("session_id")] string SessionId,
    [property: JsonPropertyName("accounts")] AccountResource[]? Accounts,
    [property: JsonPropertyName("aspsp")] AspspRef? Aspsp,
    [property: JsonPropertyName("psu_type")] string? PsuType,
    [property: JsonPropertyName("access")] Access? Access);

internal sealed record GetSessionResponse(
    [property: JsonPropertyName("status")] string? Status,
    [property: JsonPropertyName("access")] Access? Access);

internal sealed record Balance(
    [property: JsonPropertyName("name")] string? Name,
    [property: JsonPropertyName("balance_amount")] Amount? BalanceAmount,
    [property: JsonPropertyName("balance_type")] string? BalanceType,
    [property: JsonPropertyName("reference_date")] string? ReferenceDate,
    [property: JsonPropertyName("last_change_date_time")] DateTimeOffset? LastChangeDateTime);

internal sealed record HalBalances([property: JsonPropertyName("balances")] Balance[]? Balances);

internal sealed record Party([property: JsonPropertyName("name")] string? Name);

internal sealed record BankTransactionCode(
    [property: JsonPropertyName("code")] string? Code,
    [property: JsonPropertyName("sub_code")] string? SubCode,
    [property: JsonPropertyName("description")] string? Description);

internal sealed record Transaction(
    [property: JsonPropertyName("entry_reference")] string? EntryReference,
    [property: JsonPropertyName("transaction_id")] string? TransactionId,
    [property: JsonPropertyName("transaction_amount")] Amount? TransactionAmount,
    [property: JsonPropertyName("credit_debit_indicator")] string? CreditDebitIndicator,
    [property: JsonPropertyName("status")] string? Status,
    [property: JsonPropertyName("booking_date")] string? BookingDate,
    [property: JsonPropertyName("value_date")] string? ValueDate,
    [property: JsonPropertyName("transaction_date")] string? TransactionDate,
    [property: JsonPropertyName("creditor")] Party? Creditor,
    [property: JsonPropertyName("debtor")] Party? Debtor,
    [property: JsonPropertyName("creditor_account")] AccountIdentification? CreditorAccount,
    [property: JsonPropertyName("debtor_account")] AccountIdentification? DebtorAccount,
    [property: JsonPropertyName("bank_transaction_code")] BankTransactionCode? BankTransactionCode,
    [property: JsonPropertyName("remittance_information")] string[]? RemittanceInformation,
    [property: JsonPropertyName("balance_after_transaction")] Amount? BalanceAfterTransaction,
    [property: JsonPropertyName("merchant_category_code")] string? MerchantCategoryCode,
    [property: JsonPropertyName("reference_number")] string? ReferenceNumber,
    [property: JsonPropertyName("note")] string? Note);

internal sealed record HalTransactions(
    [property: JsonPropertyName("transactions")] Transaction[]? Transactions,
    [property: JsonPropertyName("continuation_key")] string? ContinuationKey);

internal sealed record ApplicationInfo(
    [property: JsonPropertyName("name")] string? Name,
    [property: JsonPropertyName("environment")] string? Environment,
    [property: JsonPropertyName("active")] bool? Active,
    [property: JsonPropertyName("redirect_urls")] string[]? RedirectUrls);

internal sealed record ApiError(
    [property: JsonPropertyName("error")] string? Error,
    [property: JsonPropertyName("message")] string? Message);
