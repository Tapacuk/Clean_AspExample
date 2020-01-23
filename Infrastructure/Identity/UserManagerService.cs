﻿using Application.Common.Interfaces;
using Application.Common.Models;
using Infrastructure.Options;
using Infrastructure.Persistance;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using System;
using System.Collections.Generic;
using System.IdentityModel.Tokens.Jwt;
using System.Linq;
using System.Security.Claims;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Infrastructure.Identity
{
    public class UserManagerService : IUserManager
    {
        private readonly UserManager<ApplicationUser> _userManager;
        private readonly SignInManager<ApplicationUser> _signInManager;
        private readonly RoleManager<IdentityRole> _roleManager;
        private readonly JwtOptions _jwtOptions;
        private readonly TokenValidationParameters _tokenValidationParameters;
        private readonly ApplicationDbContext _context;

        public UserManagerService(UserManager<ApplicationUser> userManager, SignInManager<ApplicationUser> signInManager, 
                                  RoleManager<IdentityRole> rolemanager, JwtOptions jwtOptions,
                                  ApplicationDbContext context, TokenValidationParameters tokenValidationParameters)
        {
            _userManager = userManager;
            _signInManager = signInManager;
            _roleManager = rolemanager;
            _jwtOptions = jwtOptions;
            _context = context;
            _tokenValidationParameters = tokenValidationParameters;
        }

        public async Task<AuthenticationResult> CreateUserAsync(string userName, string email, string password)
        {
            var newUser = new ApplicationUser
            {
                Email = email,
                UserName = userName
            };

            var createdUser = await _userManager.CreateAsync(newUser, password);

            if (!createdUser.Succeeded)
            {
                return new AuthenticationResult
                {
                    Errors = createdUser.Errors.Select(x => x.Description),
                    Success = false
                };
            }

            return await GenerateAuthenticationResultForUserAsync(newUser);
        }

        public async Task<AuthenticationResult> DeleteUserAsync(string userId)
        {
            var user = _userManager.Users.SingleOrDefault(u => u.Id == userId);
            
            if(user == null)
            {
                return new AuthenticationResult
                {
                    Errors = new[] { "User does not exists" }
                };
            }

            var deletedUser =  await _userManager.DeleteAsync(user);
            if(!deletedUser.Succeeded)
            {
                return new AuthenticationResult
                {
                    Errors = deletedUser.Errors.Select(x => x.Description)
                };
            }

            return new AuthenticationResult { Success = true };
        }

        public async Task<AuthenticationResult> UpdateUserAsync(string id, string UserName, string Email, string Password)
        {
            var user = await _userManager.FindByIdAsync(id);
            if(user == null)
            {
                return new AuthenticationResult
                {
                    Errors = new[] { "User does not exists" }
                };
            }
            user.UserName = UserName;
            user.Email = Email;

            var newPassword = _userManager.PasswordHasher.HashPassword(user, Password);
            user.PasswordHash = newPassword;

            var result = await _userManager.UpdateAsync(user);
            if(!result.Succeeded)
            {
                return new AuthenticationResult
                {
                    Errors = result.Errors.Select(x => x.Description)
                };
            }
            return new AuthenticationResult { Success = true };
        }

        public async Task<AuthenticationResult> LoginUserAsync(string UserName, string Password)
        {
            var user = await _userManager.FindByNameAsync(UserName);
            if (user == null)
            {
                return new AuthenticationResult
                {
                    Errors = new[] { "User does not exists" }
                };
            }
            var authResult = await _signInManager.CheckPasswordSignInAsync(user, Password, false);
           
            if(!authResult.Succeeded)
            {
                return new AuthenticationResult
                {
                    Errors = new[] { "Unable to login" }
                };
            }

            return await GenerateAuthenticationResultForUserAsync(user);
        }

        public async Task<AuthenticationResult> LogoutUserAsync()
        {
            await _signInManager.SignOutAsync();

            return new AuthenticationResult { Success = true };
        }

        public async Task<UserDto> GetUserByNameAsync(string Name)
        {
            var user = await _userManager.FindByNameAsync(Name);
            if (user != null)
                return new UserDto { Id = user.Id, Email = user.Email, UserName = user.UserName, Password = user.PasswordHash };
            return null;
        }

        public async Task<AuthenticationResult> RefreshTokenAsync(string token, string refreshToken)
        {
            var validatedToken = GetPrincipalFromToken(token);

            if (validatedToken == null)
            {
                return new AuthenticationResult { Errors = new[] { "Invalid Token" } };
            }

            var expiryDateUnix =
                long.Parse(validatedToken.Claims.Single(x => x.Type == JwtRegisteredClaimNames.Exp).Value);

            var expiryDateTimeUtc = new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc)
                .AddSeconds(expiryDateUnix);

            if (expiryDateTimeUtc > DateTime.UtcNow)
            {
                return new AuthenticationResult { Errors = new[] { "This token hasn't expired yet" } };
            }

            var jti = validatedToken.Claims.Single(x => x.Type == JwtRegisteredClaimNames.Jti).Value;

            var storedRefreshToken = await _context.RefreshTokens.SingleOrDefaultAsync(x => x.Token == refreshToken);

            if (storedRefreshToken == null)
            {
                return new AuthenticationResult { Errors = new[] { "This refresh token does not exist" } };
            }

            if (DateTime.UtcNow > storedRefreshToken.ExpiryDate)
            {
                return new AuthenticationResult { Errors = new[] { "This refresh token has expired" } };
            }

            if (storedRefreshToken.Invalidated)
            {
                return new AuthenticationResult { Errors = new[] { "This refresh token has been invalidated" } };
            }

            if (storedRefreshToken.Used)
            {
                return new AuthenticationResult { Errors = new[] { "This refresh token has been used" } };
            }

            if (storedRefreshToken.JwtId != jti)
            {
                return new AuthenticationResult { Errors = new[] { "This refresh token does not match this JWT" } };
            }

            storedRefreshToken.Used = true;
            _context.RefreshTokens.Update(storedRefreshToken);
            await _context.SaveChangesAsync();

            var user = await _userManager.FindByIdAsync(validatedToken.Claims.Single(x => x.Type == "id").Value);
            return await GenerateAuthenticationResultForUserAsync(user);
        }

        private ClaimsPrincipal GetPrincipalFromToken(string token)
        {
            var tokenHandler = new JwtSecurityTokenHandler();

            try
            {
                var tokenValidationParameters = _tokenValidationParameters.Clone();
                tokenValidationParameters.ValidateLifetime = false;
                var principal = tokenHandler.ValidateToken(token, tokenValidationParameters, out var validatedToken);
                if (!IsJwtWithValidSecurityAlgorithm(validatedToken))
                {
                    return null;
                }

                return principal;
            }
            catch
            {
                return null;
            }
        }

        private bool IsJwtWithValidSecurityAlgorithm(SecurityToken validatedToken)
        {
            return (validatedToken is JwtSecurityToken jwtSecurityToken) &&
                   jwtSecurityToken.Header.Alg.Equals(SecurityAlgorithms.HmacSha256,
                       StringComparison.InvariantCultureIgnoreCase);
        }

        private async Task<AuthenticationResult> GenerateAuthenticationResultForUserAsync(ApplicationUser user)
        {
            var tokenHandler = new JwtSecurityTokenHandler();
            var key = Encoding.ASCII.GetBytes(_jwtOptions.Secret);

            var claims = new List<Claim>
            {
                new Claim(JwtRegisteredClaimNames.Sub, user.Email),
                new Claim(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString()),
                new Claim(JwtRegisteredClaimNames.Email, user.Email),
                new Claim("id", user.Id)
            };

            var userClaims = await _userManager.GetClaimsAsync(user);
            claims.AddRange(userClaims);

            var userRoles = await _userManager.GetRolesAsync(user);
            foreach (var userRole in userRoles)
            {
                claims.Add(new Claim(ClaimTypes.Role, userRole));
                var role = await _roleManager.FindByNameAsync(userRole);
                if (role == null) continue;
                var roleClaims = await _roleManager.GetClaimsAsync(role);

                foreach (var roleClaim in roleClaims)
                {
                    if (claims.Contains(roleClaim))
                        continue;

                    claims.Add(roleClaim);
                }
            }

            var tokenDescriptor = new SecurityTokenDescriptor
            {
                Subject = new ClaimsIdentity(claims),
                Expires = DateTime.UtcNow.Add(_jwtOptions.TokenLifetime),
                SigningCredentials =
                    new SigningCredentials(new SymmetricSecurityKey(key), SecurityAlgorithms.HmacSha256Signature)
            };

            var token = tokenHandler.CreateToken(tokenDescriptor);

            var refreshToken = new RefreshToken
            {
                JwtId = token.Id,
                UserId = user.Id,
                CreationDate = DateTime.UtcNow,
                ExpiryDate = DateTime.UtcNow.AddMonths(6)
            };

            await _context.RefreshTokens.AddAsync(refreshToken);
            await _context.SaveChangesAsync();

            return new AuthenticationResult
            {
                Success = true,
                Token = tokenHandler.WriteToken(token),
                RefreshToken = refreshToken.Token
            };
        }
    }
}
