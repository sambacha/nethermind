using System;
using System.Collections.Generic;
using Nethermind.Core;
using Nethermind.Core.Caching;
using Nethermind.Core.Crypto;
using Nethermind.Core.Specs;
using Nethermind.Evm.Precompiles;
using Nethermind.Evm.Precompiles.Bls.Shamatar;
using Nethermind.Evm.Precompiles.Snarks.Shamatar;
using Nethermind.State;

namespace Nethermind.Evm.CodeAnalysis;

public static class CodeInfoCache
{
    private static readonly ICache<Keccak, CodeInfo> _codeCache = new LruCache<Keccak, CodeInfo>(MemoryAllowance.CodeCacheSize, MemoryAllowance.CodeCacheSize, "VM bytecodes");

    public static CodeInfo GetCachedCodeInfo(IWorldState worldState, Address codeSource, IReleaseSpec vmSpec)
    {
        IStateProvider state = worldState.StateProvider;
        if (codeSource.IsPrecompile(vmSpec))
        {
            return _precompiles[codeSource];
        }

        Keccak codeHash = state.GetCodeHash(codeSource);
        CodeInfo cachedCodeInfo = _codeCache.Get(codeHash);
        if (cachedCodeInfo == null)
        {
            byte[] code = state.GetCode(codeHash);

            if (code == null)
            {
                throw new NullReferenceException($"Code {codeHash} missing in the state for address {codeSource}");
            }

            cachedCodeInfo = new CodeInfo(code);
            _codeCache.Set(codeHash, cachedCodeInfo);
        }
        else
        {
            // need to touch code so that any collectors that track database access are informed
            state.TouchCode(codeHash);
        }

        return cachedCodeInfo;
    }

    static readonly IReadOnlyDictionary<Address, CodeInfo> _precompiles = new Dictionary<Address, CodeInfo>
    {
        [EcRecoverPrecompile.Instance.Address] = new(EcRecoverPrecompile.Instance),
        [Sha256Precompile.Instance.Address] = new(Sha256Precompile.Instance),
        [Ripemd160Precompile.Instance.Address] = new(Ripemd160Precompile.Instance),
        [IdentityPrecompile.Instance.Address] = new(IdentityPrecompile.Instance),

        [Bn256AddPrecompile.Instance.Address] = new(Bn256AddPrecompile.Instance),
        [Bn256MulPrecompile.Instance.Address] = new(Bn256MulPrecompile.Instance),
        [Bn256PairingPrecompile.Instance.Address] = new(Bn256PairingPrecompile.Instance),
        [ModExpPrecompile.Instance.Address] = new(ModExpPrecompile.Instance),

        [Blake2FPrecompile.Instance.Address] = new(Blake2FPrecompile.Instance),

        [G1AddPrecompile.Instance.Address] = new(G1AddPrecompile.Instance),
        [G1MulPrecompile.Instance.Address] = new(G1MulPrecompile.Instance),
        [G1MultiExpPrecompile.Instance.Address] = new(G1MultiExpPrecompile.Instance),
        [G2AddPrecompile.Instance.Address] = new(G2AddPrecompile.Instance),
        [G2MulPrecompile.Instance.Address] = new(G2MulPrecompile.Instance),
        [G2MultiExpPrecompile.Instance.Address] = new(G2MultiExpPrecompile.Instance),
        [PairingPrecompile.Instance.Address] = new(PairingPrecompile.Instance),
        [MapToG1Precompile.Instance.Address] = new(MapToG1Precompile.Instance),
        [MapToG2Precompile.Instance.Address] = new(MapToG2Precompile.Instance),
    };
}