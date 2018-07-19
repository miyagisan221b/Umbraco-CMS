﻿using System;
using System.ComponentModel.DataAnnotations;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Web.Http.Controllers;
using System.Web.Http.ModelBinding;
using System.Web.Security;
using Umbraco.Core;
using Umbraco.Core.Logging;
using Umbraco.Core.Models;
using Umbraco.Web.Models.ContentEditing;

namespace Umbraco.Web.Editors.Filters
{

    //internal class ContentValidationHelper : ContentItemValidationHelper<IContent, ContentItemSave>
    //{
    //    public ContentValidationHelper(ILogger logger, IUmbracoContextAccessor umbracoContextAccessor) : base(logger, umbracoContextAccessor)
    //    {
    //    }

    //    /// <summary>
    //    /// Validates that the correct information is in the request for saving a culture variant
    //    /// </summary>
    //    /// <param name="postedItem"></param>
    //    /// <param name="actionContext"></param>
    //    /// <returns></returns>
    //    protected override bool ValidateCultureVariant(ContentItemSave postedItem, HttpActionContext actionContext)
    //    {
    //        var contentType = postedItem.PersistedContent.GetContentType();
    //        if (contentType.VariesByCulture() && postedItem.Culture.IsNullOrWhiteSpace())
    //        {
    //            //we cannot save a content item that is culture variant if no culture was specified in the request!
    //            actionContext.Response = actionContext.Request.CreateValidationErrorResponse($"No culture found in request. Cannot save a content item that varies by culture, without a specified culture.");
    //            return false;
    //        }
    //        return true;
    //    }
    //}

    /// <summary>
    /// Custom validation helper so that we can exclude the Member.StandardPropertyTypeStubs from being validating for existence
    /// </summary>
    internal class MemberValidationHelper : ContentItemValidationHelper<IMember, MemberSave>
    {
        public MemberValidationHelper(ILogger logger, IUmbracoContextAccessor umbracoContextAccessor) : base(logger, umbracoContextAccessor)
        {
        }

        /// <summary>
        /// We need to manually validate a few things here like email and login to make sure they are valid and aren't duplicates
        /// </summary>
        /// <param name="model"></param>
        /// <param name="modelState"></param>
        /// <returns></returns>
        public override bool ValidatePropertyData(MemberSave model, ModelStateDictionary modelState)
        {
            if (model.Username.IsNullOrWhiteSpace())
            {
                modelState.AddPropertyError(
                    new ValidationResult("Invalid user name", new[] { "value" }),
                    $"{Constants.PropertyEditors.InternalGenericPropertiesPrefix}login");
            }

            if (model.Email.IsNullOrWhiteSpace() || new EmailAddressAttribute().IsValid(model.Email) == false)
            {
                modelState.AddPropertyError(
                    new ValidationResult("Invalid email", new[] { "value" }),
                    $"{Constants.PropertyEditors.InternalGenericPropertiesPrefix}email");
            }

            //default provider!
            var membershipProvider = Core.Security.MembershipProviderExtensions.GetMembersMembershipProvider();

            var validEmail = ValidateUniqueEmail(model, membershipProvider);
            if (validEmail == false)
            {
                modelState.AddPropertyError(
                    new ValidationResult("Email address is already in use", new[] { "value" }),
                    $"{Constants.PropertyEditors.InternalGenericPropertiesPrefix}email");
            }

            var validLogin = ValidateUniqueLogin(model, membershipProvider);
            if (validLogin == false)
            {
                modelState.AddPropertyError(
                    new ValidationResult("Username is already in use", new[] { "value" }),
                    $"{Constants.PropertyEditors.InternalGenericPropertiesPrefix}login");
            }

            return base.ValidatePropertyData(model, modelState);
        }

        /// <summary>
        /// This ensures that the internal membership property types are removed from validation before processing the validation
        /// since those properties are actually mapped to real properties of the IMember.
        /// This also validates any posted data for fields that are sensitive.
        /// </summary>
        /// <param name="model"></param>
        /// <param name="actionContext"></param>
        /// <returns></returns>
        protected override bool ValidateProperties(MemberSave model, HttpActionContext actionContext)
        {
            var propertiesToValidate = model.Properties.ToList();
            var defaultProps = Constants.Conventions.Member.GetStandardPropertyTypeStubs();
            var exclude = defaultProps.Select(x => x.Value.Alias).ToArray();
            foreach (var remove in exclude)
            {
                propertiesToValidate.RemoveAll(property => property.Alias == remove);
            }
            
            //if the user doesn't have access to sensitive values, then we need to validate the incoming properties to check
            //if a sensitive value is being submitted.
            if (UmbracoContextAccessor.UmbracoContext.Security.CurrentUser.HasAccessToSensitiveData() == false)
            {
                var sensitiveProperties = model.PersistedContent.ContentType
                    .PropertyTypes.Where(x => model.PersistedContent.ContentType.IsSensitiveProperty(x.Alias))
                    .ToList();

                foreach (var sensitiveProperty in sensitiveProperties)
                {
                    var prop = propertiesToValidate.FirstOrDefault(x => x.Alias == sensitiveProperty.Alias);

                    if (prop != null)
                    {
                        //this should not happen, this means that there was data posted for a sensitive property that
                        //the user doesn't have access to, which means that someone is trying to hack the values.

                        var message = $"property with alias: {prop.Alias} cannot be posted";
                        actionContext.Response = actionContext.Request.CreateErrorResponse(HttpStatusCode.NotFound, new InvalidOperationException(message));
                        return false;
                    }
                }
            }

            return ValidateProperties(propertiesToValidate, model.PersistedContent.Properties.ToList(), actionContext);
        }

        internal bool ValidateUniqueLogin(MemberSave model, MembershipProvider membershipProvider)
        {
            if (model == null) throw new ArgumentNullException(nameof(model));
            if (membershipProvider == null) throw new ArgumentNullException(nameof(membershipProvider));

            int totalRecs;
            var existingByName = membershipProvider.FindUsersByName(model.Username.Trim(), 0, int.MaxValue, out totalRecs);
            switch (model.Action)
            {
                case ContentSaveAction.Save:

                    //ok, we're updating the member, we need to check if they are changing their login and if so, does it exist already ?
                    if (model.PersistedContent.Username.InvariantEquals(model.Username.Trim()) == false)
                    {
                        //they are changing their login name
                        if (existingByName.Cast<MembershipUser>().Select(x => x.UserName)
                            .Any(x => x == model.Username.Trim()))
                        {
                            //the user cannot use this login
                            return false;
                        }
                    }
                    break;
                case ContentSaveAction.SaveNew:
                    //check if the user's login already exists
                    if (existingByName.Cast<MembershipUser>().Select(x => x.UserName)
                        .Any(x => x == model.Username.Trim()))
                    {
                        //the user cannot use this login
                        return false;
                    }
                    break;
                default:
                    //we don't support this for members
                    throw new ArgumentOutOfRangeException();
            }

            return true;
        }

        internal bool ValidateUniqueEmail(MemberSave model, MembershipProvider membershipProvider)
        {
            if (model == null) throw new ArgumentNullException(nameof(model));
            if (membershipProvider == null) throw new ArgumentNullException(nameof(membershipProvider));

            if (membershipProvider.RequiresUniqueEmail == false)
            {
                return true;
            }
            
            int totalRecs;
            var existingByEmail = membershipProvider.FindUsersByEmail(model.Email.Trim(), 0, int.MaxValue, out totalRecs);
            switch (model.Action)
            {
                case ContentSaveAction.Save:
                    //ok, we're updating the member, we need to check if they are changing their email and if so, does it exist already ?
                    if (model.PersistedContent.Email.InvariantEquals(model.Email.Trim()) == false)
                    {
                        //they are changing their email
                        if (existingByEmail.Cast<MembershipUser>().Select(x => x.Email)
                            .Any(x => x.InvariantEquals(model.Email.Trim())))
                        {
                            //the user cannot use this email
                            return false;
                        }
                    }
                    break;
                case ContentSaveAction.SaveNew:
                    //check if the user's email already exists
                    if (existingByEmail.Cast<MembershipUser>().Select(x => x.Email)
                        .Any(x => x.InvariantEquals(model.Email.Trim())))
                    {
                        //the user cannot use this email
                        return false;
                    }
                    break;
                default:
                    //we don't support this for members
                    throw new ArgumentOutOfRangeException();
            }

            return true;
        }
    }
}
