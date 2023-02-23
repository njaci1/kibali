﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;

namespace Kibali
{
    public class ProtectedResource
    {
        // Permission -> (Methods,Scheme) -> Path  (Darrel's format)
        // (Schemes -> Permissions) -> restriction -> target  (Kanchan's format)
        // target -> restrictions -> schemes -> Ordered Permissions (CSDL Format) 

        // path -> Method -> Schemes -> Permissions  (Inverted format) 

        // (Path, Method) -> Schemes -> Permissions (Docs)
        // (Path, Method) -> Scheme(delegated) -> Permissions (Graph Explorer Tab)
        // Permissions(delegated) (Graph Explorer Permissions List)
        // Schemas -> Permissions ( AAD Onboarding)
        private Dictionary<string, Dictionary<string, HashSet<string>>> leastPrivilegedPermissions { get; set; } = new();

        public string Url { get; set; }
        public Dictionary<string, Dictionary<string, List<AcceptableClaim>>> SupportedMethods { get; set; } = new Dictionary<string, Dictionary<string, List<AcceptableClaim>>>();

        public Dictionary<(string, string), HashSet<string>> PermissionMethods {get; set;} = new();
        public ProtectedResource(string url)
        {
            Url = url;
        }

        public void AddRequiredClaims(string permission, PathSet pathSet, string[] leastPrivilegedPermissionSchemes)
        {
            foreach (var supportedMethod in pathSet.Methods)
            {
                var supportedSchemes = new Dictionary<string, List<AcceptableClaim>>();
                foreach (var supportedScheme in pathSet.SchemeKeys)
                {
                    if (!supportedSchemes.ContainsKey(supportedScheme))
                    {
                        supportedSchemes.Add(supportedScheme, new List<AcceptableClaim>());
                    }

                    if (!this.PermissionMethods.TryAdd((permission, supportedScheme), new HashSet<string> { supportedMethod }))
                    {
                        this.PermissionMethods[(permission, supportedScheme)].Add(supportedMethod);
                    }

                    var isLeastPrivilege = leastPrivilegedPermissionSchemes.Contains(supportedScheme);
                    supportedSchemes[supportedScheme].Add(new AcceptableClaim(permission, pathSet.AlsoRequires, isLeastPrivilege));
                }
                if (!this.SupportedMethods.ContainsKey(supportedMethod))
                {
                    this.SupportedMethods.Add(supportedMethod, supportedSchemes);
                } else
                {
                    Update(this.SupportedMethods[supportedMethod], supportedSchemes);
                };
            }
        }

        public IEnumerable<PermissionsError> ValidateLeastPrivilegePermissions(string permission, PathSet pathSet, string[] leastPrivilegedPermissionSchemes)
        {
            ComputeLeastPrivilegeEntries(permission, pathSet, leastPrivilegedPermissionSchemes);
            var mismatchedSchemes = ValidateMismatchedSchemes(permission, pathSet, leastPrivilegedPermissionSchemes);
            var duplicateErrors = ValidateDuplicatedScopes();
            return mismatchedSchemes.Union(duplicateErrors);
        }

        

        private void ComputeLeastPrivilegeEntries(string permission, PathSet pathSet, IEnumerable<string> leastPrivilegedPermissionSchemes)
        {
            foreach (var supportedMethod in pathSet.Methods)
            {
                var schemeLeastPrivilegeScopes = new Dictionary<string, HashSet<string>>();
                foreach (var supportedScheme in pathSet.SchemeKeys)
                {
                    if (!leastPrivilegedPermissionSchemes.Contains(supportedScheme))
                    {
                        continue;
                    }
                    if (!schemeLeastPrivilegeScopes.ContainsKey(supportedScheme))
                    {
                        schemeLeastPrivilegeScopes.Add(supportedScheme, new HashSet<string>());
                    }
                    schemeLeastPrivilegeScopes[supportedScheme].Add(permission);
                }
                if (!this.leastPrivilegedPermissions.TryGetValue(supportedMethod, out var methodLeastPrivilegeScopes))
                {
                    this.leastPrivilegedPermissions.Add(supportedMethod, schemeLeastPrivilegeScopes);
                }
                else
                {
                    UpdatePrivilegedPermissions(methodLeastPrivilegeScopes, schemeLeastPrivilegeScopes, supportedMethod);
                }   
            }
        }

        private HashSet<PermissionsError> ValidateDuplicatedScopes()
        {
            var errors = new HashSet<PermissionsError>();
            foreach (var methodScopes in this.leastPrivilegedPermissions)
            {
                var method = methodScopes.Key;
                foreach (var schemeScope in methodScopes.Value)
                {
                    var scopes = schemeScope.Value;
                    var scheme = schemeScope.Key;
                    if (scopes.Count > 1 && !IsFalsePositiveDuplicate(method, scopes))
                    {
                        errors.Add(new PermissionsError
                        {
                            Path = this.Url,
                            ErrorCode = PermissionsErrorCode.DuplicateLeastPrivilegeScopes,
                            Message = string.Format(StringConstants.DuplicateLeastPrivilegeSchemeErrorMessage, string.Join(", ", scopes), scheme, method),
                        });
                    }
                }
            }
            return errors;
        }

        /// <summary>
        /// Check if the duplicate is a false positive.
        /// </summary>
        /// <param name="method">HTTP Method.</param>
        /// <param name="scopes">Duplicated permission scopes.</param>
        /// <returns>True if the duplicate is a false positive (invalid).</returns>
        private bool IsFalsePositiveDuplicate(string method, HashSet<string> scopes)
        {
            // GET operations can be done by ReadWrite permissions but we should only have one Read permission
            // which is the least privileged for Read operations.
            if (method == "GET")
            {
                var groupedOperations = scopes.GroupBy(x => x.Split('.')[1]).ToDictionary(g => g.Key, g => g.Count());
                groupedOperations.TryGetValue("Read", out int readCount);
                groupedOperations.TryGetValue("ReadBasic", out int readBasicCount);
                readCount += readBasicCount;
                return readCount == 1;
            }
            return false;
        }

        private HashSet<PermissionsError> ValidateMismatchedSchemes(string permission, PathSet pathSet, IEnumerable<string> leastPrivilegePermissionSchemes)
        {
            var mismatchedPrivilegeSchemes = leastPrivilegePermissionSchemes.Except(pathSet.SchemeKeys);
            var errors = new HashSet<PermissionsError>();
            if (mismatchedPrivilegeSchemes.Any())
            {
                var invalidSchemes = string.Join(", ", mismatchedPrivilegeSchemes);
                var expectedSchemes = string.Join(", ", pathSet.SchemeKeys);
                errors.Add(new PermissionsError
                {
                    Path = this.Url,
                    ErrorCode = PermissionsErrorCode.InvalidLeastPrivilegeScheme,
                    Message = string.Format(StringConstants.UnexpectedLeastPrivilegeSchemeErrorMessage, invalidSchemes, permission, expectedSchemes),
                });
            }
            return errors;
        }

        private void UpdatePrivilegedPermissions(Dictionary<string, HashSet<string>> existingPermissions, Dictionary<string, HashSet<string>> newPermissions, string method)
        {
            foreach (var newPermission in newPermissions)
            {
                if (existingPermissions.TryGetValue(newPermission.Key, out var existingPermission))
                {
                    existingPermission.UnionWith(newPermission.Value);
                }
                else
                {
                    existingPermissions[newPermission.Key] = newPermission.Value;
                }
            }
        }

        private void Update(Dictionary<string, List<AcceptableClaim>> existingSchemes, Dictionary<string, List<AcceptableClaim>> newSchemes)
        {
            
            foreach(var newScheme in newSchemes)
            {
                if (existingSchemes.TryGetValue(newScheme.Key, out var existingScheme))
                {
                    existingScheme.AddRange(newScheme.Value);
                } 
                else
                {
                    existingSchemes[newScheme.Key] = newScheme.Value;
                }
            }
        }

        public void Write(Utf8JsonWriter writer)
        {
            writer.WriteStartObject();
            writer.WritePropertyName("url");
            writer.WriteStringValue(Url);
            writer.WritePropertyName("methods");
            WriteSupportedMethod(writer, this.SupportedMethods);
            
            writer.WriteEndObject();
        }

        private void WriteSupportedMethod(Utf8JsonWriter writer, Dictionary<string, Dictionary<string, List<AcceptableClaim>>> supportedMethods)
        {
            writer.WriteStartObject();
            foreach (var item in supportedMethods)
            {
                writer.WritePropertyName(item.Key);
                WriteSupportedSchemes(writer, item.Value);
            }
            writer.WriteEndObject();
        }

        public void WriteSupportedSchemes(Utf8JsonWriter writer, Dictionary<string, List<AcceptableClaim>> methodClaims)
        {
            writer.WriteStartObject();
            foreach (var item in methodClaims)
            {
                writer.WritePropertyName(item.Key);
                WriteAcceptableClaims(writer, item.Value);
            }
            writer.WriteEndObject();
        }

        public void WriteAcceptableClaims(Utf8JsonWriter writer, List<AcceptableClaim> schemes)
        {
            writer.WriteStartArray();
            foreach (var item in schemes.OrderByDescending(c => c.Least))
            {
                item.Write(writer);
            }
            writer.WriteEndArray();
        }

        public string GeneratePermissionsTable(Dictionary<string, List<AcceptableClaim>> methodClaims)
        {
            var permissionsStub = new List<string> { "**TODO: Provide applicable permissions.**" };
            var markdownBuilder = new MarkDownBuilder();
            markdownBuilder.StartTable("Permission type", "Permissions (from least to most privileged)");

            var delegatedWorkScopes = methodClaims.TryGetValue("DelegatedWork", out List<AcceptableClaim> claims) ? claims.OrderByDescending(c => c.Least).Select(c => c.Permission) : permissionsStub;
            markdownBuilder.AddTableRow("Delegated (work or school account)", string.Join(", ", delegatedWorkScopes));

            var delegatedPersonalScopes = methodClaims.TryGetValue("DelegatedPersonal", out claims) ? claims.OrderByDescending(c => c.Least).Select(c => c.Permission) : permissionsStub;
            markdownBuilder.AddTableRow("Delegated (personal Microsoft account)", string.Join(", ", delegatedPersonalScopes));

            var appOnlyScopes = methodClaims.TryGetValue("Application", out claims) ? claims.OrderByDescending(c => c.Least).Select(c => c.Permission) : permissionsStub;
            markdownBuilder.AddTableRow("Application", string.Join(", ", appOnlyScopes));
            markdownBuilder.EndTable();
            return markdownBuilder.ToString();
        }

        public string FetchLeastPrivilege(string method = null, string scheme = null)
        {
            var output = string.Empty;
            var leastPrivilege = new Dictionary<string, Dictionary<string, HashSet<string>>>();
            if (method != null && scheme != null)
            {
                leastPrivilege.TryAdd(method, new Dictionary<string, HashSet<string>>());
                var permissions = this.SupportedMethods[method][scheme].Where(p => p.Least == true).Select(p => p.Permission).ToHashSet();
                PopulateLeastPrivilege(leastPrivilege, method, scheme, permissions);
            }
            if (method != null && scheme == null)
            {
                this.SupportedMethods.TryGetValue(method, out var supportedSchemes);
                if (supportedSchemes == null)
                {
                    return output;
                }
                foreach (var supportedScheme in supportedSchemes.OrderBy(s => Enum.Parse(typeof(SchemeType), s.Key)))
                {
                    leastPrivilege.TryAdd(method, new Dictionary<string, HashSet<string>>());
                    var permissions = supportedScheme.Value.Where(p => p.Least == true).Select(p => p.Permission).ToHashSet();
                    PopulateLeastPrivilege(leastPrivilege, method, supportedScheme.Key, permissions);
                }
            }
            if (method == null && scheme != null)
            {
                foreach (var supportedMethod in this.SupportedMethods.OrderBy(s => s.Key))
                {
                    supportedMethod.Value.TryGetValue(scheme, out var supportedSchemeClaims);
                    if (supportedSchemeClaims == null)
                    {
                        continue;
                    }
                    leastPrivilege.TryAdd(supportedMethod.Key, new Dictionary<string, HashSet<string>>());
                    var permissions = supportedSchemeClaims.Where(p => p.Least == true).Select(p => p.Permission).ToHashSet();
                    PopulateLeastPrivilege(leastPrivilege, supportedMethod.Key, scheme, permissions);
                }
            }
            if (method == null && scheme == null)
            {
                foreach (var supportedMethod in this.SupportedMethods.OrderBy(s => s.Key))
                {
                    foreach (var supportedScheme in supportedMethod.Value.OrderBy(s => Enum.Parse(typeof(SchemeType), s.Key)))
                    {
                        leastPrivilege.TryAdd(supportedMethod.Key, new Dictionary<string, HashSet<string>>());
                        var permissions = supportedScheme.Value.Where(p => p.Least == true).Select(p => p.Permission).ToHashSet();
                        PopulateLeastPrivilege(leastPrivilege, supportedMethod.Key, supportedScheme.Key, permissions);
                    }
                }
            }
            var builder = new StringBuilder();
            foreach (var methodEntry in leastPrivilege)
            {
                builder.AppendLine();
                builder.AppendLine(methodEntry.Key);
                foreach (var schemeEntry in methodEntry.Value)
                {
                    builder.AppendLine($"|{schemeEntry.Key} |{string.Join(";", schemeEntry.Value)}|");
                    builder.AppendLine();
                }
                builder.AppendLine();
            }
            output = builder.ToString();
            return output;
        }

        private void PopulateLeastPrivilege(Dictionary<string, Dictionary<string, HashSet<string>>> leastPrivilege, string method, string scheme, HashSet<string> permissions)
        {
            if (permissions.Count == 0)
            {
                return;
            }
            leastPrivilege[method][scheme] = Disambiguate(method, scheme, permissions);
        }

        private HashSet<string> Disambiguate(string method, string scheme, HashSet<string> permissions)
        {
            if (permissions.Count > 1)
            {
                foreach (var perm in permissions)
                {
                    if (!(this.PermissionMethods.TryGetValue((perm, scheme), out HashSet<string> perms) && perms.Count == 1))
                    {
                        continue;
                    }
                    if (perms.First() == method)
                    {
                        return new HashSet<string> { perm };
                    }
                }
            }
            return permissions;
        }
    }
}
