// <auto-generated>
// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License. See License.txt in the project root for
// license information.
//
// Code generated by Microsoft (R) AutoRest Code Generator 1.0.0.0
// Changes may cause incorrect behavior and will be lost if the code is
// regenerated.
// </auto-generated>

namespace Microsoft.Azure.IIoT.Opc.Vault.Models
{
    using Newtonsoft.Json;
    using System.Collections;
    using System.Collections.Generic;
    using System.Linq;

    /// <summary>
    /// Trust group registration collection model
    /// </summary>
    public partial class TrustGroupRegistrationListApiModel
    {
        /// <summary>
        /// Initializes a new instance of the
        /// TrustGroupRegistrationListApiModel class.
        /// </summary>
        public TrustGroupRegistrationListApiModel()
        {
            CustomInit();
        }

        /// <summary>
        /// Initializes a new instance of the
        /// TrustGroupRegistrationListApiModel class.
        /// </summary>
        /// <param name="registrations">Group registrations</param>
        /// <param name="nextPageLink">Next link</param>
        public TrustGroupRegistrationListApiModel(IList<TrustGroupRegistrationApiModel> registrations = default(IList<TrustGroupRegistrationApiModel>), string nextPageLink = default(string))
        {
            Registrations = registrations;
            NextPageLink = nextPageLink;
            CustomInit();
        }

        /// <summary>
        /// An initialization method that performs custom operations like setting defaults
        /// </summary>
        partial void CustomInit();

        /// <summary>
        /// Gets or sets group registrations
        /// </summary>
        [JsonProperty(PropertyName = "registrations")]
        public IList<TrustGroupRegistrationApiModel> Registrations { get; set; }

        /// <summary>
        /// Gets or sets next link
        /// </summary>
        [JsonProperty(PropertyName = "nextPageLink")]
        public string NextPageLink { get; set; }

    }
}