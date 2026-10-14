using System;
using Framework.Constants.Net;

namespace Framework.Attributes
{
    [AttributeUsage(AttributeTargets.Method)]
    public sealed class AuthMessageAttribute : Attribute
    {
		public AuthClientMessage Message { get; set; }
		public AuthChannel Channel { get; set; }

		public AuthMessageAttribute(AuthClientMessage message, AuthChannel channel)
		{
			Message = message;
			Channel = channel;
		}
    }
}
