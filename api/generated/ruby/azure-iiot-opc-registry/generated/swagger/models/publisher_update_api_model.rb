# encoding: utf-8
# Copyright (c) Microsoft Corporation. All rights reserved.
# Licensed under the MIT License. See License.txt in the project root for
# license information.
#
# Code generated by Microsoft (R) AutoRest Code Generator 1.0.0.0
# Changes may cause incorrect behavior and will be lost if the code is
# regenerated.

module azure.iiot.opc.registry
  module Models
    #
    # Publisher registration update request
    #
    class PublisherUpdateApiModel
      # @return [String] Site of the publisher
      attr_accessor :site_id

      # @return [PublisherConfigApiModel]
      attr_accessor :configuration

      # @return [TraceLogLevel] Possible values include: 'Error',
      # 'Information', 'Debug', 'Verbose'
      attr_accessor :log_level


      #
      # Mapper for PublisherUpdateApiModel class as Ruby Hash.
      # This will be used for serialization/deserialization.
      #
      def self.mapper()
        {
          client_side_validation: true,
          required: false,
          serialized_name: 'PublisherUpdateApiModel',
          type: {
            name: 'Composite',
            class_name: 'PublisherUpdateApiModel',
            model_properties: {
              site_id: {
                client_side_validation: true,
                required: false,
                serialized_name: 'siteId',
                type: {
                  name: 'String'
                }
              },
              configuration: {
                client_side_validation: true,
                required: false,
                serialized_name: 'configuration',
                type: {
                  name: 'Composite',
                  class_name: 'PublisherConfigApiModel'
                }
              },
              log_level: {
                client_side_validation: true,
                required: false,
                serialized_name: 'logLevel',
                type: {
                  name: 'Enum',
                  module: 'TraceLogLevel'
                }
              }
            }
          }
        }
      end
    end
  end
end